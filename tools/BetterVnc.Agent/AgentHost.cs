using System.Net;
using BetterVnc.Agent.Input;
using BetterVnc.Agent.Pairing;
using BetterVnc.Agent.Protocol;
using BetterVnc.Platform.Clipboards;

namespace BetterVnc.Agent;

/// <summary>
/// Everything a running agent consists of: the config file, the clipboard and input back ends, the server and a log.
/// The tray icon (Windows), the menu bar item (macOS) and the console front end all drive this one object; settings
/// changes go through <see cref="SaveAndRestartAsync"/>.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly object _logLock = new();
    private readonly LinkedList<string> _recentLog = new();
    private AgentServer? _server;
    private int _connected;
    private readonly string? _videoUnavailable;

    public AgentHost(string configPath, AgentConfig config, bool verbose)
    {
        ConfigPath = configPath;
        Config = config;
        Verbose = verbose;
        LogPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "bettervnc-agent.log");
        Version = typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "?";
        Clipboard = CreateClipboard();
        try { Input = CreateInput(config); }
        catch (Exception ex) { Log("Input injection unavailable: " + ex.Message); }
        if (config.EnableVideo)
        {
            try { Video = CreateVideo(); }
            catch (Exception ex)
            {
                _videoUnavailable = ex.Message;
                Log("Screen streaming unavailable: " + ex.Message);
            }
        }
        Approver = CreateApprover();
    }

    public string ConfigPath { get; }
    public AgentConfig Config { get; }
    public bool Verbose { get; }
    public string LogPath { get; }
    public string Version { get; }
    public IGuestClipboard Clipboard { get; }
    public IGuestInput? Input { get; }
    /// <summary>Screen capture and encoder for the viewer's stream modes; null when off or not available on this OS.</summary>
    public IGuestVideoSource? Video { get; }
    public IPairingApprover Approver { get; }
    public bool IsListening => _server != null;
    public int Port => _server?.Port ?? Config.Port;
    public int ConnectedViewers => _server?.SessionCount ?? 0;
    public string? LastError { get; private set; }

    /// <summary>One formatted log line (already time-stamped).</summary>
    public event Action<string>? Logged;
    /// <summary>Something the UI shows changed: listening state, connections, viewers, settings.</summary>
    public event Action? Changed;
    /// <summary>A pairing was approved.</summary>
    public event Action<PairedViewer>? Paired;
    /// <summary>macOS: a stream or a scroll was refused for want of Screen Recording or Accessibility; from any thread.</summary>
    public event Action? PermissionsNeeded;

    public string StatusLine
    {
        get
        {
            if (_server == null) return LastError != null ? $"Not running: {LastError}" : "Stopped";
            return $"Listening on {Config.Listen}:{_server.Port}";
        }
    }

    public string ViewersLine
    {
        get
        {
            int paired;
            lock (Config.Viewers) paired = Config.Viewers.Count;
            var auth = string.IsNullOrEmpty(Config.Token) ? (Config.AllowPairing ? "pairing" : "no way to connect") : (Config.AllowPairing ? "pairing or token" : "token only");
            return $"{ConnectedViewers} connected, {paired} paired ({auth})";
        }
    }

    public IReadOnlyList<string> RecentLog { get { lock (_logLock) return _recentLog.ToArray(); } }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Binds the listener; throws when the port cannot be opened.</summary>
    public void Start()
    {
        if (_server != null) return;
        if (!IPAddress.TryParse(Config.Listen, out var address)) throw new InvalidOperationException($"'{Config.Listen}' is not an IP address.");
#if WINDOWS
        // So the settings window's switches apply with the restart it does, without restarting the agent.
        if (Input is WindowsInput windowsInput)
        {
            windowsInput.SupportsScroll = Config.EnableScroll;
            windowsInput.SupportsPointerAndKeys = Config.EnableInput;
        }
#endif
        var server = new AgentServer(Clipboard, new AgentServerOptions
        {
            Token = string.IsNullOrWhiteSpace(Config.Token) ? null : Config.Token.Trim(),
            FindViewer = Config.FindViewer,
            ViewerPaired = OnViewerPaired,
            PairingApprover = Approver,
            AllowPairing = Config.AllowPairing,
            Port = Config.Port,
            ListenAddress = address,
            MaxPayloadBytes = Math.Max(1, Config.MaxPayloadMb) * 1024L * 1024L,
            StagingDirectory = Config.StagingDirectory ?? Path.Combine(Path.GetTempPath(), "bettervnc-agent"),
            AppName = $"BetterVNC agent {Version}",
        }, Input, Video);
        server.Log += OnServerLog;
        // LastSeen is updated in memory only: writing the config on every connection could overwrite edits made with
        // the command line while the agent runs. Pairings and settings changes are what gets saved.
        server.ViewerConnected += (id, name, peer) => { Interlocked.Increment(ref _connected); Changed?.Invoke(); };
        try { server.Start(); }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log($"Cannot listen on {Config.Listen}:{Config.Port}: {ex.Message}");
            Changed?.Invoke();
            throw;
        }
        LastError = null;
        _server = server;
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        var s = _server;
        _server = null;
        if (s != null) await s.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Writes the config and restarts the listener so every setting takes effect. Connected viewers reconnect by themselves.</summary>
    public async Task SaveAndRestartAsync()
    {
        Config.Save(ConfigPath);
        await StopAsync().ConfigureAwait(false);
        try { Start(); }
        catch { /* LastError is set and shown */ }
        Log($"Settings applied: port {Config.Port}, listen {Config.Listen}, {Config.AuthSummary}");
    }

    public void SaveQuietly()
    {
        try { Config.Save(ConfigPath); } catch (Exception ex) { Log("Could not save config: " + ex.Message); }
    }

    private void OnViewerPaired(PairedViewer viewer)
    {
        Config.AddViewer(viewer);
        SaveQuietly();
        Paired?.Invoke(viewer);
        Changed?.Invoke();
    }

    public bool RemoveViewer(string idOrName)
    {
        var removed = Config.RemoveViewer(idOrName);
        if (removed) { SaveQuietly(); Log($"Removed paired viewer {idOrName}"); Changed?.Invoke(); }
        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        (Input as IDisposable)?.Dispose();
        (Clipboard as IDisposable)?.Dispose();
    }

    // ------------------------------------------------------------------ log

    private void OnServerLog(string m)
    {
        if (!Verbose && (m.StartsWith("Clipboard changed") || m.StartsWith("Clipboard set") || m.StartsWith("Scroll:"))) return;
        Log(m);
        if (m.EndsWith("disconnected") || m.Contains(": connected")) Changed?.Invoke();
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (_logLock)
        {
            _recentLog.AddLast(line);
            while (_recentLog.Count > 200) _recentLog.RemoveFirst();
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
        try { Logged?.Invoke(line); } catch { } // a broken console must never take a session down
    }

    // ------------------------------------------------------------------ back ends

    private static IGuestClipboard CreateClipboard()
    {
#if WINDOWS
        return new WindowsClipboard();
#else
        if (OperatingSystem.IsMacOS()) return new MacClipboard();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return new LinuxClipboard();
        throw new PlatformNotSupportedException("This build of the agent supports Linux and macOS; use the Windows build on Windows.");
#endif
    }

    private IGuestInput? CreateInput(AgentConfig cfg)
    {
#if WINDOWS
        return cfg.EnableScroll || cfg.EnableInput ? new WindowsInput(cfg.ScrollScale, cfg.EnableScroll, cfg.EnableInput) : null;
#else
        if (!cfg.EnableScroll) return null; // pointer and keys are Windows only so far
        if (OperatingSystem.IsMacOS())
            return new MacInput(cfg.ScrollScale, cfg.ScrollInvert, cfg.ScrollPhases) { Log = Log, AccessibilityMissing = PermissionsNeededHandler };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return LinuxInput.TryCreate(cfg.ScrollScale, Log);
        return null;
#endif
    }

    private IGuestVideoSource? CreateVideo()
    {
#if WINDOWS
        return new Video.WindowsScreenSource { Log = Log };
#else
        if (OperatingSystem.IsMacOS()) return new Video.MacScreenSource { Log = Log, PermissionMissing = () => PermissionsNeededHandler() };
        return null; // a Linux (PipeWire) source is still to come
#endif
    }

    /// <summary>Raises <see cref="PermissionsNeeded"/>; false when nothing listens (console mode), so the caller can fall back to macOS's own prompt.</summary>
    private bool PermissionsNeededHandler()
    {
        var handlers = PermissionsNeeded;
        if (handlers == null) return false;
        try { handlers(); } catch (Exception ex) { Log("Permission wizard failed: " + ex.Message); }
        return true;
    }

    public string VideoDescription => Video switch
    {
#if WINDOWS
        Video.WindowsScreenSource w => w.Description,
#else
        Video.MacScreenSource m => m.Description,
#endif
        null => !Config.EnableVideo ? "off" : _videoUnavailable != null ? "unavailable: " + _videoUnavailable : "not available on this OS yet",
        _ => Video.GetType().Name,
    };

    public string InputDescription => Input switch
    {
#if WINDOWS
        WindowsInput w => w.Description,
#else
        MacInput m => m.Description,
        LinuxInput l => l.Description,
#endif
        null => "off",
        _ => Input.GetType().Name,
    };

    private IPairingApprover CreateApprover()
    {
#if WINDOWS
        return new WindowsApprover();
#else
        if (OperatingSystem.IsMacOS()) return new MacApprover();
        return new LinuxApprover(Log);
#endif
    }
}
