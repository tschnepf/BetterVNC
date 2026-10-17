using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Clipboards;

/// <summary>
/// Windows clipboard through Windows Forms. Every call is marshalled onto one dedicated STA thread because OLE
/// clipboard access requires it. Change detection uses the clipboard sequence number, which is cheap and exact.
/// </summary>
public sealed class WindowsClipboard : IGuestClipboard, IDisposable
{
    private const string PngFormat = "PNG";
    private const string DropEffectFormat = "Preferred DropEffect";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public WindowsClipboard()
    {
        _thread = new Thread(() => { foreach (var work in _queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "clipboard-sta",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public string OsName => "windows";
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public string? GetChangeToken()
    {
        uint seq = GetClipboardSequenceNumber();
        return seq == 0 ? null : seq.ToString();
    }

    public IReadOnlyList<string> GetFormats() => OnSta(() =>
    {
        var list = new List<string>();
        var data = Clipboard.GetDataObject();
        if (data == null) return list;
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)) list.Add(AgentFormat.Text);
        if (data.GetDataPresent(DataFormats.Html)) list.Add(AgentFormat.Html);
        if (data.GetDataPresent(PngFormat) || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib)) list.Add(AgentFormat.Png);
        if (data.GetDataPresent(DataFormats.FileDrop)) list.Add(AgentFormat.Files);
        return list;
    });

    public byte[]? GetData(string format) => OnSta<byte[]?>(() =>
    {
        switch (format)
        {
            case AgentFormat.Text:
                return Clipboard.ContainsText() ? Encoding.UTF8.GetBytes(Clipboard.GetText(TextDataFormat.UnicodeText)) : null;
            case AgentFormat.Html:
                if (!Clipboard.ContainsText(TextDataFormat.Html)) return null;
                return Encoding.UTF8.GetBytes(StripCfHtmlHeader(Clipboard.GetText(TextDataFormat.Html)));
            case AgentFormat.Png:
                return GetPng();
            default:
                return null;
        }
    });

    private static byte[]? GetPng()
    {
        var data = Clipboard.GetDataObject();
        if (data == null) return null;
        // Browsers, Paint.NET, Office and so on put a real PNG on the clipboard; prefer it because it keeps alpha.
        if (data.GetDataPresent(PngFormat))
        {
            switch (data.GetData(PngFormat))
            {
                case MemoryStream ms: return ms.ToArray();
                case byte[] bytes: return bytes;
                case Stream s: { using var copy = new MemoryStream(); s.CopyTo(copy); return copy.ToArray(); }
            }
        }
        if (!Clipboard.ContainsImage()) return null;
        using var image = Clipboard.GetImage();
        if (image == null) return null;
        using var buffer = new MemoryStream();
        image.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    public IReadOnlyList<string>? GetFilePaths() => OnSta<IReadOnlyList<string>?>(() =>
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var list = Clipboard.GetFileDropList();
        var result = new List<string>(list.Count);
        foreach (var p in list) if (!string.IsNullOrEmpty(p)) result.Add(p);
        return result;
    });

    public void SetData(string format, byte[] data) => OnSta(() =>
    {
        var obj = new DataObject();
        switch (format)
        {
            case AgentFormat.Text:
                obj.SetText(Encoding.UTF8.GetString(data), TextDataFormat.UnicodeText);
                break;
            case AgentFormat.Html:
                // Windows Forms writes the CF_HTML header for us.
                var html = Encoding.UTF8.GetString(data);
                obj.SetText(html, TextDataFormat.Html);
                obj.SetText(StripTags(html), TextDataFormat.UnicodeText);
                break;
            case AgentFormat.Png:
            {
                obj.SetData(PngFormat, false, new MemoryStream(data));
                using var source = Image.FromStream(new MemoryStream(data));
                // CF_DIB consumers (Paint, Office) show transparent pixels as black; flatten onto white for them.
                var flat = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.DrawImage(source, 0, 0, source.Width, source.Height);
                }
                obj.SetImage(flat);
                break;
            }
            default:
                throw new ArgumentException($"Unsupported format '{format}'.");
        }
        Clipboard.SetDataObject(obj, true, 10, 100);
        return true;
    });

    public void SetFiles(IReadOnlyList<string> paths) => OnSta(() =>
    {
        var list = new StringCollection();
        foreach (var p in paths) list.Add(p);
        var obj = new DataObject();
        obj.SetFileDropList(list);
        obj.SetData(DropEffectFormat, false, new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY, so Explorer copies rather than moves
        Clipboard.SetDataObject(obj, true, 10, 100);
        return true;
    });

    /// <summary>CF_HTML has a text header (Version, StartHTML, ...); return only the fragment or the document.</summary>
    internal static string StripCfHtmlHeader(string cfHtml)
    {
        const string start = "<!--StartFragment-->", end = "<!--EndFragment-->";
        int s = cfHtml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        int e = cfHtml.IndexOf(end, StringComparison.OrdinalIgnoreCase);
        if (s >= 0 && e > s) return cfHtml.Substring(s + start.Length, e - s - start.Length);
        int html = cfHtml.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (html >= 0) return cfHtml[html..];
        int lt = cfHtml.IndexOf('<');
        return lt >= 0 ? cfHtml[lt..] : cfHtml;
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        foreach (var c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    private T OnSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => _queue.CompleteAdding();
}
