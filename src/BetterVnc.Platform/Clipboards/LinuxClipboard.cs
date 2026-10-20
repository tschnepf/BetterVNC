using System.Security.Cryptography;
using System.Text;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Platform.Clipboards;

/// <summary>
/// Linux clipboard through <c>wl-paste</c>/<c>wl-copy</c> (Wayland) or <c>xclip</c> (X11). Neither protocol has a cheap
/// change counter a separate process can read, so the change token is a hash of the clipboard's target list plus the
/// content of the most relevant target, polled once a second.
/// </summary>
public sealed class LinuxClipboard : ILocalClipboard
{
    private readonly bool _wayland;
    private readonly ClipboardPoller _poller;

    public LinuxClipboard()
    {
        _poller = new ClipboardPoller(this);
        bool haveWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool haveX11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        if (haveWayland && ProcessRunner.Exists("wl-paste") && ProcessRunner.Exists("wl-copy")) _wayland = true;
        else if (haveX11 && ProcessRunner.Exists("xclip")) _wayland = false;
        else if (haveWayland) throw new InvalidOperationException("Wayland session detected but wl-clipboard is not installed (apt install wl-clipboard).");
        else if (haveX11) throw new InvalidOperationException("X11 session detected but xclip is not installed (apt install xclip).");
        else throw new InvalidOperationException("No DISPLAY or WAYLAND_DISPLAY in the environment; the agent must run inside the desktop session.");
    }

    /// <summary>Polled every <see cref="PollInterval"/>: the helper tools offer no change notification.</summary>
    public event Action? Changed { add => _poller.Add(value); remove => _poller.Remove(value); }

    public string OsName => "linux";
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);
    public string Backend => _wayland ? "wl-clipboard" : "xclip";

    // ------------------------------------------------------------------ targets

    private List<string> ListTargets()
    {
        var r = _wayland
            ? ProcessRunner.Run("wl-paste", new[] { "--list-types" })
            : ProcessRunner.Run("xclip", new[] { "-selection", "clipboard", "-o", "-t", "TARGETS" });
        if (!r.Ok) return new List<string>(); // empty clipboard (or no owner)
        return r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string? TextTarget(List<string> t) =>
        t.FirstOrDefault(x => x.StartsWith("text/plain;charset=utf-8", StringComparison.OrdinalIgnoreCase))
        ?? t.FirstOrDefault(x => x == "UTF8_STRING")
        ?? t.FirstOrDefault(x => x.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        ?? t.FirstOrDefault(x => x is "STRING" or "TEXT");

    private static string? HtmlTarget(List<string> t) => t.FirstOrDefault(x => x.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
    private static string? PngTarget(List<string> t) => t.FirstOrDefault(x => x == "image/png");
    private static string? FilesTarget(List<string> t) => t.FirstOrDefault(x => x == "x-special/gnome-copied-files") ?? t.FirstOrDefault(x => x == "text/uri-list");

    private byte[]? Read(string target)
    {
        var r = _wayland
            ? ProcessRunner.Run("wl-paste", new[] { "--no-newline", "--type", target })
            : ProcessRunner.Run("xclip", new[] { "-selection", "clipboard", "-o", "-t", target });
        return r.Ok ? r.Stdout : null;
    }

    private void Write(string? target, byte[] data)
    {
        if (_wayland)
        {
            var args = new List<string>();
            if (target != null) { args.Add("--type"); args.Add(target); }
            ProcessRunner.RunDetached("wl-copy", args, data);
        }
        else
        {
            var args = new List<string> { "-selection", "clipboard", "-i" };
            if (target != null) { args.Add("-t"); args.Add(target); }
            ProcessRunner.RunDetached("xclip", args, data);
        }
    }

    // ------------------------------------------------------------------ IGuestClipboard

    public string? GetChangeToken()
    {
        var targets = ListTargets();
        if (targets.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var t in targets.Where(t => !t.StartsWith("TIMESTAMP") && t != "TARGETS" && t != "MULTIPLE" && t != "SAVE_TARGETS")) sb.Append(t).Append('\n');
        var primary = PngTarget(targets) ?? FilesTarget(targets) ?? HtmlTarget(targets) ?? TextTarget(targets);
        if (primary != null)
        {
            var bytes = Read(primary);
            if (bytes != null) sb.Append(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public IReadOnlyList<string> GetFormats()
    {
        var t = ListTargets();
        var list = new List<string>();
        if (TextTarget(t) != null) list.Add(AgentFormat.Text);
        if (HtmlTarget(t) != null) list.Add(AgentFormat.Html);
        if (PngTarget(t) != null) list.Add(AgentFormat.Png);
        if (FilesTarget(t) != null) list.Add(AgentFormat.Files);
        return list;
    }

    public byte[]? GetData(string format)
    {
        var t = ListTargets();
        var target = format switch
        {
            AgentFormat.Text => TextTarget(t),
            AgentFormat.Html => HtmlTarget(t),
            AgentFormat.Png => PngTarget(t),
            _ => null,
        };
        return target == null ? null : Read(target);
    }

    public IReadOnlyList<string>? GetFilePaths()
    {
        var t = ListTargets();
        var target = FilesTarget(t);
        if (target == null) return null;
        var raw = Read(target);
        if (raw == null) return null;
        var paths = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line == "copy" || line == "cut" || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile) paths.Add(uri.LocalPath);
            else if (line.StartsWith('/')) paths.Add(line);
        }
        return paths.Count == 0 ? null : paths;
    }

    public void SetData(string format, byte[] data)
    {
        switch (format)
        {
            case AgentFormat.Text: Write(null, data); break;
            case AgentFormat.Html: Write("text/html", data); break;
            case AgentFormat.Png: Write("image/png", data); break;
            default: throw new ArgumentException($"Unsupported format '{format}'.");
        }
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        var uris = paths.Select(p => new Uri(Path.GetFullPath(p)).AbsoluteUri);
        // xclip and wl-copy serve one target each. GNOME's Files wants its own format; everything else takes text/uri-list.
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            Write("x-special/gnome-copied-files", Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", uris)));
        else
            Write("text/uri-list", Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"));
    }
}
