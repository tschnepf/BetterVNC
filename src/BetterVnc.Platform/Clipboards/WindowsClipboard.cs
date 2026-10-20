using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Platform.Clipboards;

/// <summary>
/// Windows clipboard through Windows Forms, for the agent and the viewer alike. Every call is marshalled onto one
/// dedicated STA thread because OLE clipboard access requires it. Change detection uses the clipboard sequence number,
/// which is cheap and exact; <see cref="Changed"/> comes from a clipboard format listener.
/// </summary>
public sealed class WindowsClipboard : ILocalClipboard, IDisposable
{
    private const string PngFormat = "PNG";
    private const string DropEffectFormat = "Preferred DropEffect";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly object _listenerLock = new();
    private Action? _changed;
    private ChangeListener? _listener;

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

    /// <summary>
    /// From a message-only window registered as a clipboard format listener (WM_CLIPBOARDUPDATE), on a thread of its own
    /// that the first handler starts. The agent polls the sequence number instead and never starts it.
    /// </summary>
    public event Action? Changed
    {
        add
        {
            lock (_listenerLock)
            {
                _changed += value;
                if (_listener != null) return;
                try { _listener = ChangeListener.Start(() => Volatile.Read(ref _changed)?.Invoke()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("BetterVNC clipboard: no change listener: " + ex.Message); }
            }
        }
        remove { lock (_listenerLock) _changed -= value; }
    }

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

    public void Dispose()
    {
        lock (_listenerLock) _listener?.Stop();
        _queue.CompleteAdding();
    }

    /// <summary>A message-only window with a message loop of its own, told about every clipboard change.</summary>
    private sealed class ChangeListener : NativeWindow
    {
        private const int WM_CLOSE = 0x0010, WM_CLIPBOARDUPDATE = 0x031D;
        private static readonly IntPtr HWND_MESSAGE = new(-3);
        private readonly Action _changed;

        private ChangeListener(Action changed) => _changed = changed;

        public static ChangeListener Start(Action changed)
        {
            var listener = new ChangeListener(changed);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    listener.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
                    if (!AddClipboardFormatListener(listener.Handle)) throw new Win32Exception();
                    ready.SetResult();
                }
                catch (Exception ex)
                {
                    if (listener.Handle != IntPtr.Zero) listener.DestroyHandle();
                    ready.SetException(ex);
                    return;
                }
                Application.Run();
            })
            {
                IsBackground = true,
                Name = "clipboard-listener",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Task.GetAwaiter().GetResult();
            return listener;
        }

        public void Stop()
        {
            var hwnd = Handle;
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_CLIPBOARDUPDATE:
                    try { _changed(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("BetterVNC clipboard: change handler failed: " + ex.Message); }
                    return;
                case WM_CLOSE:
                    RemoveClipboardFormatListener(Handle);
                    DestroyHandle();
                    Application.ExitThread();
                    return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
