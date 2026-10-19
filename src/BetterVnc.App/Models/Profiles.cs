using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BetterVnc.App.Models;

public enum ConnectionKind
{
    Generic,
    UnraidVm,
    Mac,
}

public enum ScaleMode
{
    Fit,
    OneToOne,
}

public enum MacModifierMode
{
    /// <summary>Win = Command, Alt = Option, Ctrl = Control (physical layout).</summary>
    Standard,
    /// <summary>Ctrl = Command so Ctrl+C/V/Z act like Cmd+C/V/Z; Win = Control.</summary>
    CtrlAsCommand,
    /// <summary>Alt = Command, Win = Option (Command sits next to the space bar like on a Mac keyboard).</summary>
    SwapWinAlt,
}

public enum FileTransferMode
{
    None,
    Sftp,
    Smb,
}

public enum AgentAuthMode
{
    /// <summary>Pair once: the agent shows a dialog in the guest, the user clicks Allow, both sides keep a derived key.</summary>
    Pair,
    /// <summary>Use the token or password set in the agent's settings.</summary>
    Token,
}

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ConnectionProfile : ObservableBase
{
    private string _name = "";
    private string _host = "";
    private int _port = 5900;
    private ConnectionKind _kind;
    private DateTime? _lastConnected;
    private int _openSessions;
    private int _connectedSessions;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public ConnectionKind Kind { get => _kind; set { if (Set(ref _kind, value)) Raise(nameof(KindLabel)); } }
    public string? Username { get; set; }

    /// <summary>DPAPI-protected password; null when none is stored.</summary>
    public string? ProtectedPassword { get; set; }

    public DateTime? LastConnected { get => _lastConnected; set { if (Set(ref _lastConnected, value)) Raise(nameof(Subtitle)); } }

    // Unraid linkage
    public string? UnraidServerId { get; set; }
    public string? UnraidVmName { get; set; }

    // Display / input
    public ScaleMode ScaleMode { get; set; } = ScaleMode.Fit;
    public bool StartFullscreen { get; set; }
    public MacModifierMode MacModifiers { get; set; } = MacModifierMode.Standard;
    public bool CaptureWindowsKeys { get; set; } = true;
    public bool SyncClipboard { get; set; } = true;
    public bool EnableAudio { get; set; } = true;
    public bool LocalCursor { get; set; } = true;

    /// <summary>VNC wheel clicks per physical wheel notch (1-20). macOS moves only about a line per click, so 6 or more feels normal there.</summary>
    public int ScrollClicksPerNotch { get; set; } = 1;
    /// <summary>Preferred picture source and quality; a stream mode falls back to lossless VNC until the agent can stream.</summary>
    public PictureMode PictureMode { get; set; } = PictureMode.VncLossless;
    /// <summary>Tight compression level derived from <see cref="PictureMode"/>; kept so older builds still read the profile.</summary>
    public int CompressionLevel { get; set; } = 1;
    /// <summary>Tight JPEG quality derived from <see cref="PictureMode"/> (-1 = lossless); kept so older builds still read the profile.</summary>
    public int JpegQuality { get; set; } = -1;

    // File transfer side channel
    public FileTransferMode FileTransfer { get; set; } = FileTransferMode.None;
    public string? SftpHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string? SftpUsername { get; set; }
    public string? SftpProtectedPassword { get; set; }
    public string? SftpPrivateKeyPath { get; set; }
    public string? RemoteDropFolder { get; set; }
    public string? SmbPath { get; set; }
    public string? SmbUsername { get; set; }
    public string? SmbProtectedPassword { get; set; }

    // Guest agent (rich clipboard: images, files, HTML)
    public bool AgentEnabled { get; set; }
    /// <summary>
    /// No VNC server: the picture is the agent's stream and keyboard and mouse go through the agent, for a Windows
    /// machine that runs only the agent. Host and port above are then not used; the agent's host (or the VNC host
    /// field, when that is empty) is. For Unraid VMs this ignores the VNC console.
    /// </summary>
    public bool AgentOnly { get; set; }
    /// <summary>Host the agent listens on; null means the VNC host. Unraid VMs need the guest's own address here.</summary>
    public string? AgentHost { get; set; }
    public int AgentPort { get; set; } = 5970;
    /// <summary>How the viewer proves itself to the agent: pairing (approved once in the guest) or the agent's token.</summary>
    public AgentAuthMode AgentAuth { get; set; } = AgentAuthMode.Pair;
    /// <summary>DPAPI-protected shared token or password set in the agent (token mode).</summary>
    public string? AgentProtectedToken { get; set; }
    /// <summary>DPAPI-protected key derived when this connection's agent approved the pairing; null until paired.</summary>
    public string? AgentProtectedPairKey { get; set; }

    /// <summary>Number of session windows currently open for this profile (runtime only, maintained by SessionManager).</summary>
    [JsonIgnore]
    public int OpenSessions
    {
        get => _openSessions;
        set { if (Set(ref _openSessions, value)) { Raise(nameof(IsOpen)); Raise(nameof(SessionBadge)); } }
    }

    /// <summary>Number of those windows with a live VNC connection (runtime only).</summary>
    [JsonIgnore]
    public int ConnectedSessions
    {
        get => _connectedSessions;
        set { if (Set(ref _connectedSessions, value)) { Raise(nameof(IsConnected)); Raise(nameof(SessionBadge)); } }
    }

    [JsonIgnore]
    public bool IsOpen => OpenSessions > 0;

    [JsonIgnore]
    public bool IsConnected => ConnectedSessions > 0;

    /// <summary>Text for the status badge in the connection list; meaningful only while IsOpen.</summary>
    [JsonIgnore]
    public string SessionBadge => (IsConnected ? "Connected" : "Disconnected") + (OpenSessions > 1 ? $" ×{OpenSessions}" : "");

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        ConnectionKind.UnraidVm => AgentOnly ? "Unraid VM, agent" : "Unraid VM",
        ConnectionKind.Mac => "macOS",
        _ => AgentOnly ? "Agent" : "VNC",
    };

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var target = Kind == ConnectionKind.UnraidVm && !string.IsNullOrEmpty(UnraidVmName)
                ? $"{UnraidVmName} on {Host}"
                : AgentOnly ? $"{(string.IsNullOrWhiteSpace(AgentHost) ? Host : AgentHost)}:{AgentPort}"
                : $"{Host}:{Port}";
            return LastConnected is { } t ? $"{target}  ·  last used {t:g}" : target;
        }
    }

    public void NotifyChanged()
    {
        Raise(nameof(Name));
        Raise(nameof(Subtitle));
        Raise(nameof(KindLabel));
    }
}

public sealed class UnraidServer : ObservableBase
{
    private string _name = "";
    private string _host = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set { if (Set(ref _host, value)) Raise(nameof(Subtitle)); } }
    public int SshPort { get; set; } = 22;
    public string Username { get; set; } = "root";
    public string? ProtectedPassword { get; set; }
    public string? PrivateKeyPath { get; set; }

    [JsonIgnore]
    public string Subtitle => $"{Username}@{Host}:{SshPort}";

    public void NotifyChanged()
    {
        Raise(nameof(Name));
        Raise(nameof(Subtitle));
    }
}

public sealed class ProfileDocument
{
    /// <summary>1: JPEG quality / compression level per connection. 2: adds <see cref="ConnectionProfile.PictureMode"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public List<ConnectionProfile> Connections { get; set; } = new();
    public List<UnraidServer> UnraidServers { get; set; } = new();
}
