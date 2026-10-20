using System.Text.Json;
using System.Text.Json.Serialization;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent;

/// <summary>Settings stored in <c>bettervnc-agent.json</c> next to the executable.</summary>
public sealed class AgentConfig
{
    public const string FileName = "bettervnc-agent.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Optional shared token or password; the viewer stores the same value in the connection profile. Null: paired viewers only.</summary>
    public string? Token { get; set; }
    /// <summary>Accept new viewers that ask to pair (a dialog on this screen asks for approval).</summary>
    public bool AllowPairing { get; set; } = true;
    /// <summary>Viewers approved through pairing, with the key each one derived.</summary>
    public List<PairedViewer> Viewers { get; set; } = new();
    public int Port { get; set; } = AgentServer.DefaultPort;
    /// <summary>Address to bind: 0.0.0.0 for every interface, 127.0.0.1 for a tunnel-only setup.</summary>
    public string Listen { get; set; } = "0.0.0.0";
    public int MaxPayloadMb { get; set; } = 256;
    /// <summary>Where files pushed by the viewer are written before they are put on the clipboard.</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>Accept high-resolution scroll events from the viewer (smooth scrolling). Off means VNC wheel clicks only.</summary>
    public bool EnableScroll { get; set; } = true;
    /// <summary>Let viewers stream this screen as video (the viewer's "Stream" picture modes). Windows only so far; off means VNC pixels only.</summary>
    public bool EnableVideo { get; set; } = true;
    /// <summary>
    /// Let viewers control the keyboard and mouse through the agent, so they can connect without a VNC server (picture
    /// from the stream, input from here). Windows only so far. Off means they need VNC for that.
    /// </summary>
    public bool EnableInput { get; set; } = true;
    /// <summary>Multiplier for the scroll distance the viewer sends (1.0 = one wheel notch moves what it moves on the Windows side).</summary>
    public double ScrollScale { get; set; } = 1.0;
    /// <summary>macOS only: flip the direction, for setups where the system applies "natural" scrolling to injected events too.</summary>
    public bool ScrollInvert { get; set; }
    /// <summary>macOS only: forward gesture phases so apps treat the stream like a trackpad gesture. Turn off if an app misbehaves.</summary>
    public bool ScrollPhases { get; set; } = true;

    /// <summary>
    /// Next to the program, except inside a macOS app bundle: writing there would break the bundle's signature (and with it
    /// the privacy permissions), so the agent uses the installed location, ~/Library/Application Support/BetterVNC.
    /// </summary>
    public static string DefaultPath => MacAppBundle.EnclosingBundle(Path.Combine(AppContext.BaseDirectory, MacAppBundle.ExecutableName)) != null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "BetterVNC", FileName)
        : Path.Combine(AppContext.BaseDirectory, FileName);

    public static AgentConfig? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOptions);
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
    }

    /// <summary>A fresh config: pairing on, no token (set one with 'token generate' or the settings UI).</summary>
    public static AgentConfig CreateNew() => new();

    public PairedViewer? FindViewer(string id)
    {
        lock (Viewers) return Viewers.FirstOrDefault(v => v.Id == id);
    }

    public void AddViewer(PairedViewer viewer)
    {
        lock (Viewers)
        {
            Viewers.RemoveAll(v => v.Id == viewer.Id);
            Viewers.Add(viewer);
        }
    }

    public bool RemoveViewer(string idOrName)
    {
        lock (Viewers) return Viewers.RemoveAll(v => v.Id == idOrName || string.Equals(v.Name, idOrName, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    [JsonIgnore]
    public string AuthSummary => (AllowPairing ? "pairing on" : "pairing off") + (string.IsNullOrEmpty(Token) ? ", no token" : ", token set") + $", {Viewers.Count} paired";
}
