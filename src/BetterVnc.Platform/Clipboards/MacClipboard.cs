using System.Text;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Platform.Clipboards;

/// <summary>
/// macOS clipboard through <c>pbpaste</c>/<c>pbcopy</c> for text and <c>osascript</c> (JavaScript for Automation, which
/// can call AppKit's NSPasteboard) for images, files, HTML and the change counter. Binary data goes through temporary
/// files so nothing has to be quoted into a script.
/// </summary>
public sealed class MacClipboard : ILocalClipboard
{
    private const string Prelude = "ObjC.import('AppKit'); var pb = $.NSPasteboard.generalPasteboard; var env = $.NSProcessInfo.processInfo.environment; ";

    private readonly ClipboardPoller _poller;

    public MacClipboard() => _poller = new ClipboardPoller(this);

    /// <summary>Polled every <see cref="PollInterval"/>: the helper tools offer no change notification.</summary>
    public event Action? Changed { add => _poller.Add(value); remove => _poller.Remove(value); }

    public string OsName => "macos";
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    private static ProcessRunner.Result Jxa(string script, IDictionary<string, string>? env = null) =>
        ProcessRunner.Run("osascript", new[] { "-l", "JavaScript", "-e", Prelude + script }, env: env);

    private static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), $"bettervnc-agent-{Guid.NewGuid():N}{ext}");

    // ------------------------------------------------------------------ IGuestClipboard

    public string? GetChangeToken()
    {
        var r = Jxa("pb.changeCount");
        return r.Ok ? r.Text.Trim() : null;
    }

    private List<string> Types()
    {
        var r = Jxa("var t = pb.types; var a = []; for (var i = 0; i < t.count; i++) a.push(ObjC.unwrap(t.objectAtIndex(i))); a.join('\\n')");
        return r.Ok ? r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new List<string>();
    }

    private static bool HasText(List<string> t) => t.Contains("public.utf8-plain-text") || t.Contains("NSStringPboardType") || t.Contains("public.plain-text");
    private static bool HasHtml(List<string> t) => t.Contains("public.html") || t.Contains("Apple HTML pasteboard type");
    private static bool HasImage(List<string> t) => t.Contains("public.png") || t.Contains("public.tiff");
    private static bool HasFiles(List<string> t) => t.Contains("public.file-url") || t.Contains("NSFilenamesPboardType");

    public IReadOnlyList<string> GetFormats()
    {
        var t = Types();
        var list = new List<string>();
        if (HasText(t)) list.Add(AgentFormat.Text);
        if (HasHtml(t)) list.Add(AgentFormat.Html);
        if (HasImage(t)) list.Add(AgentFormat.Png);
        if (HasFiles(t)) list.Add(AgentFormat.Files);
        return list;
    }

    public byte[]? GetData(string format)
    {
        var t = Types();
        switch (format)
        {
            case AgentFormat.Text:
            {
                if (!HasText(t)) return null;
                var r = ProcessRunner.Run("pbpaste", Array.Empty<string>());
                return r.Ok ? r.Stdout : null;
            }
            case AgentFormat.Html:
            {
                if (!HasHtml(t)) return null;
                var r = Jxa("var s = pb.stringForType('public.html'); s.isNil() ? '' : ObjC.unwrap(s)");
                if (!r.Ok || r.Stdout.Length == 0) return null;
                var text = r.Text;
                if (text.EndsWith('\n')) text = text[..^1]; // osascript appends a newline
                return Encoding.UTF8.GetBytes(text);
            }
            case AgentFormat.Png:
            {
                if (!HasImage(t)) return null;
                var png = TempFile(".png");
                var tiff = TempFile(".tiff");
                try
                {
                    var r = Jxa(
                        "var d = pb.dataForType('public.png'); if (!d.isNil()) { d.writeToFileAtomically(env.objectForKey('BVNC_PNG'), true); 'png' } " +
                        "else { d = pb.dataForType('public.tiff'); if (d.isNil()) { 'none' } else { d.writeToFileAtomically(env.objectForKey('BVNC_TIFF'), true); 'tiff' } }",
                        new Dictionary<string, string> { ["BVNC_PNG"] = png, ["BVNC_TIFF"] = tiff });
                    if (!r.Ok) return null;
                    switch (r.Text.Trim())
                    {
                        case "png":
                            return File.ReadAllBytes(png);
                        case "tiff":
                            var conv = ProcessRunner.Run("sips", new[] { "-s", "format", "png", tiff, "--out", png });
                            return conv.Ok && File.Exists(png) ? File.ReadAllBytes(png) : null;
                        default:
                            return null;
                    }
                }
                finally
                {
                    try { File.Delete(png); } catch { }
                    try { File.Delete(tiff); } catch { }
                }
            }
            default:
                return null;
        }
    }

    public IReadOnlyList<string>? GetFilePaths()
    {
        var r = Jxa(
            "var items = pb.pasteboardItems; var out = []; for (var i = 0; i < items.count; i++) { var s = items.objectAtIndex(i).stringForType('public.file-url'); " +
            "if (!s.isNil()) { var p = $.NSURL.URLWithString(s).path; if (!p.isNil()) out.push(ObjC.unwrap(p)); } } out.join('\\n')");
        if (!r.Ok) return null;
        var paths = r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return paths.Count == 0 ? null : paths;
    }

    public void SetData(string format, byte[] data)
    {
        switch (format)
        {
            case AgentFormat.Text:
                ProcessRunner.Run("pbcopy", Array.Empty<string>(), data);
                break;
            case AgentFormat.Html:
            {
                var tmp = TempFile(".html");
                try
                {
                    File.WriteAllBytes(tmp, data);
                    var r = Jxa(
                        "var s = $.NSString.stringWithContentsOfFileEncodingError(env.objectForKey('BVNC_FILE'), $.NSUTF8StringEncoding, null); " +
                        "pb.clearContents; pb.setStringForType(s, 'public.html'); pb.setStringForType(s.js.replace(/<[^>]*>/g, ''), 'public.utf8-plain-text'); 'ok'",
                        new Dictionary<string, string> { ["BVNC_FILE"] = tmp });
                    if (!r.Ok) throw new InvalidOperationException("osascript failed: " + r.Stderr.Trim());
                }
                finally { try { File.Delete(tmp); } catch { } }
                break;
            }
            case AgentFormat.Png:
            {
                var tmp = TempFile(".png");
                try
                {
                    File.WriteAllBytes(tmp, data);
                    var r = Jxa(
                        "var d = $.NSData.dataWithContentsOfFile(env.objectForKey('BVNC_FILE')); pb.clearContents; pb.setDataForType(d, 'public.png'); 'ok'",
                        new Dictionary<string, string> { ["BVNC_FILE"] = tmp });
                    if (!r.Ok) throw new InvalidOperationException("osascript failed: " + r.Stderr.Trim());
                }
                finally { try { File.Delete(tmp); } catch { } }
                break;
            }
            default:
                throw new ArgumentException($"Unsupported format '{format}'.");
        }
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        var r = Jxa(
            "var list = ObjC.unwrap(env.objectForKey('BVNC_PATHS')).split('\\n').filter(function (p) { return p.length > 0; }); " +
            "var urls = list.map(function (p) { return $.NSURL.fileURLWithPath(p); }); pb.clearContents; pb.writeObjects($(urls)); 'ok'",
            new Dictionary<string, string> { ["BVNC_PATHS"] = string.Join("\n", paths) });
        if (!r.Ok) throw new InvalidOperationException("osascript failed: " + r.Stderr.Trim());
    }
}
