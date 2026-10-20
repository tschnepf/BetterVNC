using System.Runtime.InteropServices;
using System.Text;
using BetterVnc.Agent.Protocol;
using BetterVnc.Platform;
using BetterVnc.Protocol;
using BetterVnc.Protocol.Decoders;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;

namespace BetterVnc.Viewer.Core.Sessions;

/// <summary>What a session needs from the app and the OS.</summary>
public sealed class SessionServices
{
    /// <summary>The store the profile belongs to: its secrets, and saved when the session changes the profile.</summary>
    public required ProfileStore Profiles { get; init; }
    public required IUiThread Ui { get; init; }
    public required ILocalClipboard Clipboard { get; init; }
    /// <summary>Plays the server's audio; the session owns it and disposes of it.</summary>
    public required IAudioSink Audio { get; init; }
    /// <summary>A JPEG decoder for each connection and stream (Tight JPEG rectangles, motion JPEG frames).</summary>
    public required Func<IJpegDecoder> CreateJpegDecoder { get; init; }
    /// <summary>Names the JPEG decoder in the status bar ("WIC").</summary>
    public required string JpegDecoderName { get; init; }
}

/// <summary>
/// Everything a session window does that is not UI: the VNC connection and its authentication retry, the guest agent,
/// the picture mode (Tight quality or the agent's stream), the clipboard sync rules, file sending, and the status text.
/// The window keeps only its controls and events and implements <see cref="ISessionView"/>. Used on the UI thread.
/// </summary>
public sealed class SessionController : IDisposable
{
    public const string NoFileChannelMessage =
        "VNC itself cannot carry files. Either run the BetterVNC guest agent in the remote machine and enable it in the connection " +
        "(files land directly on the remote clipboard), or configure an SFTP/SMB drop folder.";

    private readonly ConnectionProfile _profile;
    private readonly ISessionView _view;
    private readonly SessionServices _services;
    private readonly ProfileStore _profiles;
    private readonly IUiThread _ui;
    private readonly ILocalClipboard _clipboard;
    private readonly IAudioSink _audio;
    private readonly string? _passwordOverride;
    private readonly string? _guestAddress;
    private readonly FileTransferService _files;
    private readonly IUiTimer _statusTimer;
    private AgentClient? _agent;
    private VideoStreamPlayer? _player;
    private string? _sessionUsername;
    private string? _sessionPassword;
    private string? _lastRemoteClipboard;
    private string? _lastLocalClipboard;
    private bool _audioRequested;
    private bool? _audioAvailableShown;
    private bool _clipboardListening;
    private long _lastBytes;
    private DateTime _lastBytesTime = DateTime.UtcNow;
    /// <summary>The picture mode chosen for this session (profile default until changed in the toolbar).</summary>
    private PictureModes.Info _wantedPicture;
    /// <summary>The Tight quality the RFB connection is currently asked for.</summary>
    private PictureModes.Info _activeVnc = PictureModes.StreamFallback;
    private long _jpegRectsAtQualityChange;
    private bool _disposed;

    /// <param name="hostOverride">Resolved host/port/password for Unraid VMs (looked up over SSH before opening the window).</param>
    /// <param name="guestAddress">An Unraid VM's own address, where its guest agent listens; null when not known yet.</param>
    public SessionController(ConnectionProfile profile, ISessionView view, SessionServices services,
        string? hostOverride = null, int? portOverride = null, string? passwordOverride = null, string? guestAddress = null)
    {
        _profile = profile;
        _view = view;
        _services = services;
        _profiles = services.Profiles;
        _ui = services.Ui;
        _clipboard = services.Clipboard;
        _audio = services.Audio;
        _guestAddress = guestAddress;
        // Profiles saved by older builds may still hold a "vnc://host" style value.
        var (parsedHost, parsedPort) = HostParser.Parse(hostOverride ?? profile.Host);
        Host = parsedHost;
        Port = portOverride ?? parsedPort ?? profile.Port;
        _passwordOverride = passwordOverride;
        _files = new FileTransferService(profile, _profiles.Secrets);
        _wantedPicture = PictureModes.Get(profile.PictureMode);
        SyncClipboard = profile.SyncClipboard;
        AudioWanted = profile.EnableAudio;
        _statusTimer = _ui.CreateTimer(TimeSpan.FromSeconds(1), UpdateStatus);
    }

    public ConnectionProfile Profile => _profile;
    public string Host { get; }
    public int Port { get; }

    /// <summary>The latest VNC connection: set once one succeeds, kept (disconnected) until the next one succeeds.</summary>
    public VncClient? Client { get; private set; }
    /// <summary>The guest agent of the current connection; null without one, or while disconnected.</summary>
    public AgentClient? Agent => _agent;
    /// <summary>Plays the agent's stream into the view; there whenever <see cref="Agent"/> is.</summary>
    public VideoStreamPlayer? Player => _player;

    /// <summary>True while the VNC connection is up (false while connecting, after a disconnect, or on the error overlay).</summary>
    public bool IsConnected { get; private set; }

    /// <summary><see cref="IsConnected"/> changed.</summary>
    public event Action? ConnectionChanged;

    /// <summary>The clipboard toggle: text both ways, and the remote path of files sent to the drop folder.</summary>
    public bool SyncClipboard { get; set; }

    /// <summary>The audio toggle, as last set through <see cref="SetAudio"/>.</summary>
    public bool AudioWanted { get; private set; }

    public PictureModes.Info WantedPicture => _wantedPicture;

    /// <summary>Files can go through the guest agent (preferred) or the SFTP/SMB drop folder.</summary>
    public bool CanSendFiles => _agent?.IsConnected == true || _files.IsConfigured;

    // ------------------------------------------------------------------ connection

    public async Task ConnectAsync()
    {
        _view.ShowOverlay("Connecting…", $"{Host}:{Port}", showButtons: false);
        _view.DetachSession();
        _view.SetScrollInjector(null);
        _player?.Dispose();
        _player = null;
        _agent?.Dispose();
        _agent = null;
        Client?.Dispose();
        _audioRequested = false;

        var password = _sessionPassword ?? _passwordOverride ?? _profiles.Unprotect(_profile.ProtectedPassword);
        // A stream mode starts out on lossless VNC; the stream takes over once the agent is up (see ApplyPictureAsync).
        var initialVnc = _wantedPicture.IsStream ? PictureModes.StreamFallback : _wantedPicture;
        var options = new VncConnectionOptions
        {
            Host = Host,
            Port = Port,
            Username = _sessionUsername ?? _profile.Username,
            Password = password,
            CompressionLevel = initialVnc.CompressionLevel,
            JpegQuality = initialVnc.JpegQuality,
            EnableLocalCursor = _profile.LocalCursor,
            EnableQemuAudio = true,
            JpegDecoder = _services.CreateJpegDecoder(),
            CredentialProvider = (kind, prompt) => _ui.Invoke(() => PromptCredentials(kind, prompt)),
        };
        var client = new VncClient(options);
        client.Log += m => _ui.Post(() => _view.SetStatus(m));
        client.Disconnected += ex => _ui.Post(() => OnDisconnected(ex));
        client.ClipboardTextReceived += t => _ui.Post(() => OnRemoteClipboard(t));
        client.ClipboardRequested += () => _ui.Post(PushLocalClipboard);
        client.Bell += () => _ui.Post(_view.Beep);
        client.DesktopNameChanged += n => _ui.Post(() => _view.SetTitle($"{_profile.Name}  ·  {n}"));
        client.AudioBegin += (fmt, ch, hz) => _ui.Post(() => _audio.Start(hz, ch, 16));
        client.AudioData += data => _audio.Feed(data);
        client.AudioEnd += () => _ui.Post(() => _audio.Stop());

        try
        {
            await client.ConnectAsync();
        }
        catch (VncAuthenticationException ex)
        {
            bool apple = client.Capabilities.SecurityTypeUsed == SecurityType.AppleArd;
            client.Dispose();
            _view.ShowOverlay("Authentication failed", ex.Message, showButtons: true);
            // Ask again right away, like RDP does, instead of leaving the person at a dead overlay.
            var prompt = apple
                ? $"The Mac rejected the sign-in ({ex.Message}).\n\nUse the macOS account's short name (the name of its home folder, not the full name) and that account's login password. The account must be allowed under Screen Sharing in the Mac's Sharing settings."
                : $"The server rejected the password ({ex.Message}). Enter it again.";
            var creds = PromptCredentials(apple ? CredentialKind.UsernameAndPassword : CredentialKind.Password, prompt);
            if (creds == null) return;
            _sessionUsername = creds.Username ?? _profile.Username;
            _sessionPassword = creds.Password;
            _ = ConnectAsync();
            return;
        }
        catch (Exception ex)
        {
            client.Dispose();
            _view.ShowOverlay("Could not connect", ex.Message, showButtons: true);
            return;
        }

        Client = client;
        _activeVnc = initialVnc;
        _jpegRectsAtQualityChange = 0;
        IsConnected = true;
        ConnectionChanged?.Invoke();
        if (client.Capabilities.IsApple && _profile.Kind != ConnectionKind.Mac)
            _view.UseMacKeyboard();
        _view.AttachSession(client);
        _view.HideOverlay();
        _view.FitToRemote(client.Framebuffer.Width, client.Framebuffer.Height);
        _view.SetTitle($"{_profile.Name}  ·  {client.DesktopName}");
        _view.SetStatus($"Connected  ·  {client.Capabilities}");
        _profile.LastConnected = DateTime.Now;
        try { _profiles.Save(); } catch { }

        if (!_clipboardListening)
        {
            _clipboard.Changed += OnLocalClipboardChangedAnyThread;
            _clipboardListening = true;
        }
        _statusTimer.Start();
        StartAgent();
        _view.RefreshPictureMenu();
        UpdateStatus();
        if (_wantedPicture.IsStream && _agent == null)
            _view.SetStatus("Stream mode needs the guest agent; showing VNC lossless because this connection has none configured");
    }

    /// <summary>Ends the connection (the window closes after).</summary>
    public void Disconnect() => Client?.Disconnect();

    private VncCredentials? PromptCredentials(CredentialKind kind, string prompt)
    {
        var creds = _view.PromptCredentials(kind, prompt, _profile.Username, out bool remember);
        if (creds == null) return null;
        if (remember)
        {
            if (kind == CredentialKind.UsernameAndPassword) _profile.Username = creds.Username;
            _profile.ProtectedPassword = _profiles.Protect(creds.Password);
            try { _profiles.Save(); } catch { }
        }
        return creds;
    }

    private void OnDisconnected(Exception? ex)
    {
        IsConnected = false;
        ConnectionChanged?.Invoke();
        _statusTimer.Stop();
        _audio.Stop();
        _view.SetScrollInjector(null);
        _player?.Dispose();
        _player = null;
        _agent?.Dispose();
        _agent = null;
        _view.RefreshPictureMenu();
        _view.ReleaseKeys();
        if (ex == null)
            _view.ShowOverlay("Disconnected", "The session ended.", showButtons: true);
        else
            _view.ShowOverlay("Connection lost", ex.Message, showButtons: true);
    }

    // ------------------------------------------------------------------ guest agent

    /// <summary>Connects to the BetterVNC guest agent (rich clipboard, scrolling, screen stream) once the VNC session is up, if the profile has one.</summary>
    private void StartAgent()
    {
        if (!AgentClient.IsConfigured(_profile) || Client == null) return;
        var agent = new AgentClient(_profile, Host, _profiles, _ui, _clipboard, _guestAddress, GuestAddressLookUp());
        agent.StateChanged += () => { if (_agent == agent) OnAgentStateChanged(agent); };
        agent.Message += m => { if (_agent == agent) _view.SetStatus(m); };
        agent.RemoteTextReceived += t => { if (_agent == agent) OnRemoteClipboard(t); };
        _agent = agent;
        _view.SetScrollInjector(agent);
        var player = new VideoStreamPlayer(agent, new VncClientPicture(Client), _view.Video, _ui,
            _services.CreateJpegDecoder(), _services.JpegDecoderName);
        player.Message += m => { if (_player == player) _view.SetStatus(m); };
        player.Ended += _ => { if (_player == player) { _view.RefreshPictureMenu(); UpdateStatus(); } };
        _player = player;
        _view.SetStatus($"Connecting to guest agent at {agent.Target}…");
        agent.Start();
    }

    /// <summary>
    /// For an Unraid VM the VNC host is the Unraid server, so the agent's address (the VM's own) is looked up there,
    /// again whenever it is unknown or stops answering. Null for every other kind of connection.
    /// </summary>
    private Func<CancellationToken, Task<string?>>? GuestAddressLookUp()
    {
        if (_profile.Kind != ConnectionKind.UnraidVm || _profile.UnraidVmName is not { Length: > 0 } vmName
            || _profiles.FindServer(_profile.UnraidServerId) is not { } server)
            return null;
        return ct => new UnraidService(server, _profiles.Secrets).GetGuestAddressAsync(vmName, ct);
    }

    private void OnAgentStateChanged(AgentClient agent)
    {
        _view.RefreshPictureMenu();
        UpdateStatus();
        // The agent came up (or back) and the person wants a stream: start it now, the same way clipboard features light up.
        if (_wantedPicture.IsStream && agent.CanStream && _player is { IsStreaming: false })
            _ = ApplyPictureAsync();
    }

    // ------------------------------------------------------------------ picture mode

    /// <summary>Why the current server and agent cannot deliver <paramref name="info"/>, for its menu entry; null when they can.</summary>
    public string? WhyUnavailable(PictureModes.Info info)
    {
        if (info.IsStream)
        {
            if (_agent == null || _player == null)
                return "This connection has no guest agent. Enable the agent in the connection settings; the stream modes need it.";
            return _agent.CanStream ? null : _agent.StreamUnavailableReason;
        }
        if (info.IsLossy && Client?.Capabilities is { IsApple: true })
            return "macOS Screen Sharing offers no lossy encoding (Raw, Hextile, Zlib, ZRLE only).";
        return null;
    }

    /// <summary>True when the session's picture mode differs from the profile's default.</summary>
    public bool CanMakePictureDefault => _wantedPicture.Mode != _profile.PictureMode;

    /// <summary>The person picked a picture mode in the toolbar.</summary>
    public void SelectPicture(PictureModes.Info info)
    {
        _wantedPicture = info;
        _ = ApplyPictureAsync();
    }

    public void MakePictureDefault()
    {
        _profile.PictureMode = _wantedPicture.Mode;
        var vnc = _wantedPicture.IsStream ? PictureModes.StreamFallback : _wantedPicture;
        _profile.JpegQuality = vnc.JpegQuality;
        _profile.CompressionLevel = vnc.CompressionLevel;
        try { _profiles.Save(); } catch { }
        _view.RefreshPictureMenu();
        _view.SetStatus($"\"{_wantedPicture.Label}\" is now the default picture for {_profile.Name}");
    }

    /// <summary>
    /// Makes the picture match <see cref="WantedPicture"/>: a VNC mode changes the Tight encodings on the live connection; a
    /// stream mode starts (or re-tunes) the agent stream, or falls back to lossless VNC while the agent cannot stream.
    /// </summary>
    public async Task ApplyPictureAsync()
    {
        var client = Client;
        if (client == null) return;
        var mode = _wantedPicture;
        try
        {
            if (mode.IsStream)
            {
                if (_player != null && _agent?.CanStream == true)
                {
                    SetVncQuality(client, PictureModes.StreamFallback);
                    await _player.StartAsync(mode);
                }
                else
                {
                    if (_player?.IsStreaming == true) await _player.StopAsync();
                    SetVncQuality(client, PictureModes.StreamFallback);
                    _view.SetStatus(_agent == null
                        ? "Stream mode needs the guest agent; showing VNC lossless because this connection has none configured"
                        : $"Showing VNC lossless until the guest agent can stream: {_agent.StreamUnavailableReason}");
                }
            }
            else
            {
                if (_player?.IsStreaming == true) await _player.StopAsync();
                SetVncQuality(client, mode);
                _view.SetStatus(mode.IsLossy
                    ? $"Picture: {mode.Label} (Tight JPEG {mode.JpegQuality}). If the status bar never shows JPEG, the server ignores lossy requests (QEMU needs lossy=on)."
                    : $"Picture: {mode.Label}");
            }
        }
        catch (Exception ex)
        {
            _view.SetStatus("Picture: " + ex.Message);
        }
        _view.RefreshPictureMenu();
        UpdateStatus();
    }

    private void SetVncQuality(VncClient client, PictureModes.Info quality)
    {
        if (client.Options.JpegQuality == quality.JpegQuality && client.Options.CompressionLevel == quality.CompressionLevel)
        {
            _activeVnc = quality;
            return;
        }
        _activeVnc = quality;
        _jpegRectsAtQualityChange = client.Statistics.TightJpegRects;
        client.SetPictureQuality(quality.CompressionLevel, quality.JpegQuality);
    }

    // ------------------------------------------------------------------ clipboard

    private void OnLocalClipboardChangedAnyThread() => _ui.Post(OnLocalClipboardChanged);

    private void OnLocalClipboardChanged()
    {
        if (_disposed || Client == null || !SyncClipboard) return;
        if (_agent?.IsOwnClipboardChange() == true) return; // the agent just put guest content here
        PushLocalClipboard();
        if (_agent?.IsConnected == true) _ = MirrorLocalImageAsync();
    }

    /// <summary>An image copied locally (screenshot, browser, Paint) goes straight to the guest clipboard through the agent.</summary>
    private async Task MirrorLocalImageAsync()
    {
        var agent = _agent;
        if (agent == null) return;
        try
        {
            bool hasText = false, hasFiles = false, hasImage = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var formats = _clipboard.GetFormats();
                    hasText = formats.Contains(AgentFormat.Text);
                    hasFiles = formats.Contains(AgentFormat.Files);
                    hasImage = formats.Contains(AgentFormat.Png);
                    break;
                }
                catch (ExternalException) { await Task.Delay(30); } // another application holds the clipboard open
            }
            // Text goes through PushLocalClipboard; a copy that carries text and a picture (Excel, Word) is almost always meant as text.
            if (!hasImage || hasText || hasFiles) return;
            await agent.PushClipboardImageAsync();
        }
        catch (Exception ex)
        {
            _view.SetStatus("Guest clipboard: " + ex.Message);
        }
    }

    private void PushLocalClipboard()
    {
        var client = Client;
        if (client == null) return;
        string? text = null;
        for (int attempt = 0; attempt < 3 && text == null; attempt++)
        {
            try
            {
                if (_clipboard.GetData(AgentFormat.Text) is { } utf8) text = Encoding.UTF8.GetString(utf8);
                else return;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        if (string.IsNullOrEmpty(text) || text == _lastRemoteClipboard || text == _lastLocalClipboard) return;
        _lastLocalClipboard = text;
        // Through the agent when it is connected: UTF-8, and it works where the VNC server keeps no clipboard of its own
        // (QEMU without a qemu-vdagent channel, as on most Unraid VMs). Over VNC otherwise.
        if (_agent is { IsConnected: true } agent) _ = PushTextThroughAgentAsync(agent, client, text);
        else client.SendClipboardText(text);
    }

    private static async Task PushTextThroughAgentAsync(AgentClient agent, VncClient client, string text)
    {
        bool sent;
        try { sent = await agent.PushTextAsync(text); }
        catch (Exception) { sent = false; }
        if (!sent) client.SendClipboardText(text);
    }

    private void OnRemoteClipboard(string text)
    {
        if (!SyncClipboard || string.IsNullOrEmpty(text) || text == _lastRemoteClipboard) return;
        _lastRemoteClipboard = text;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _clipboard.SetData(AgentFormat.Text, Encoding.UTF8.GetBytes(text));
                _view.SetStatus("Clipboard received from remote");
                return;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
    }

    /// <summary>The toolbar's Paste: files and images go through the agent or the file channel, text as a clipboard push.</summary>
    public async Task PasteToRemoteAsync()
    {
        if (Client == null) return;
        try
        {
            var formats = _clipboard.GetFormats();
            if (formats.Contains(AgentFormat.Files))
            {
                await UploadAsync(_clipboard.GetFilePaths() ?? Array.Empty<string>());
            }
            else if (formats.Contains(AgentFormat.Png))
            {
                if (_agent?.IsConnected == true)
                {
                    if (await _agent.PushClipboardImageAsync()) return;
                }
                var png = _clipboard.GetData(AgentFormat.Png);
                if (png == null) return;
                var tmp = Path.Combine(Path.GetTempPath(), $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                File.WriteAllBytes(tmp, png);
                await UploadAsync(new[] { tmp });
                try { File.Delete(tmp); } catch { }
            }
            else if (formats.Contains(AgentFormat.Text))
            {
                _lastLocalClipboard = null;
                PushLocalClipboard();
                _view.SetStatus("Clipboard text sent to remote");
            }
        }
        catch (Exception ex)
        {
            _view.ShowMessage("Paste to remote", ex.Message, warning: true);
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>Sends files to the remote: onto its clipboard through the agent, or into the SFTP/SMB drop folder.</summary>
    public async Task UploadAsync(IReadOnlyList<string> files)
    {
        if (_agent?.IsConnected == true)
        {
            try
            {
                await _agent.PushFilesAsync(files, new Progress<string>(m => _view.SetStatus(m)));
                _view.SetStatus($"{files.Count} item(s) placed on the guest clipboard; paste them in the remote");
            }
            catch (Exception ex)
            {
                _view.SetStatus("Send failed");
                _view.ShowMessage("Send files to guest", ex.Message, warning: true);
            }
            return;
        }
        if (!_files.IsConfigured)
        {
            _view.ShowMessage("Send files", NoFileChannelMessage, warning: false);
            return;
        }
        var progress = new Progress<(string file, double fraction)>(p => _view.SetStatus($"Uploading {p.file}  {p.fraction:P0}"));
        try
        {
            var written = await _files.UploadAsync(files, progress, CancellationToken.None);
            _view.SetStatus($"Uploaded {written.Count} file(s) via {_files.Description}");
            // Put the remote path on the remote clipboard so it can be pasted straight away.
            if (written.Count > 0 && Client != null && SyncClipboard)
                Client.SendClipboardText(string.Join("\n", written));
        }
        catch (Exception ex)
        {
            _view.SetStatus("Upload failed");
            _view.ShowMessage("Upload failed", ex.Message, warning: true);
        }
    }

    // ------------------------------------------------------------------ audio

    /// <summary>The audio toggle changed.</summary>
    public void SetAudio(bool enabled)
    {
        AudioWanted = enabled;
        if (Client == null) return;
        if (enabled) { Client.SetAudioEnabled(true); _audioRequested = true; }
        else { Client.SetAudioEnabled(false); _audio.Stop(); }
    }

    private void ShowAudioAvailable(bool available)
    {
        if (_audioAvailableShown == available) return;
        _audioAvailableShown = available;
        _view.SetAudioAvailable(available);
    }

    // ------------------------------------------------------------------ status

    /// <summary>The view drew a framebuffer update: audio can be switched on once the server has shown it supports it.</summary>
    public void OnRenderedFrame()
    {
        var client = Client;
        if (client == null) return;
        if (!_audioRequested && client.Capabilities.QemuAudio && AudioWanted)
        {
            _audioRequested = true;
            client.SetAudioEnabled(true);
        }
        if (client.Capabilities.QemuAudio) ShowAudioAvailable(true);
    }

    /// <summary>Refreshes the status bar's statistics; runs every second while connected.</summary>
    public void UpdateStatus()
    {
        var client = Client;
        if (client == null) return;
        var s = client.Statistics;
        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.001, (now - _lastBytesTime).TotalSeconds);
        double rate = (s.BytesReceived - _lastBytes) / seconds;
        _lastBytes = s.BytesReceived;
        _lastBytesTime = now;

        var caps = client.Capabilities;
        var enc = s.TightRects > 0 ? "Tight" : s.ZrleRects > 0 ? "ZRLE" : s.HextileRects > 0 ? "Hextile" : s.ZlibRects > 0 ? "Zlib" : s.RawRects > 0 ? "Raw" : "-";
        var features = new List<string>();
        if (caps.QemuExtendedKeyEvents) features.Add("raw keys");
        if (caps.ExtendedClipboard) features.Add("ext clipboard");
        if (caps.QemuAudio) features.Add("audio");
        if (caps.LocalCursor) features.Add("local cursor");
        if (_agent != null) features.Add(_agent.IsConnected ? "agent" : _agent.Status);
        ShowAudioAvailable(caps.QemuAudio);

        string picture;
        string traffic;
        if (_player is { IsStreaming: true } player)
        {
            var info = player.Info;
            double streamRate = player.TakeRate();
            picture = player.IdleFallbackActive
                ? $"stream idle, {enc} lossless"
                : $"stream {info?.Codec} {info?.Width}×{info?.Height}" + (player.DecodePath is { } path ? $" ({path})" : "") + (player.FramesSkipped > 0 ? $" ({player.FramesSkipped} skipped)" : "");
            traffic = $"{FormatRate(streamRate)} stream + {FormatRate(rate)} VNC";
        }
        else
        {
            var q = _activeVnc;
            picture = !q.IsLossy ? $"{enc} lossless"
                : s.TightJpegRects > _jpegRectsAtQualityChange ? $"{enc} JPEG {q.JpegQuality}"
                : $"{enc}, JPEG {q.JpegQuality} requested (none received yet)";
            traffic = FormatRate(rate);
        }
        if (_wantedPicture.IsStream && _player is not { IsStreaming: true }) picture += "  (waiting for agent stream)";

        _view.SetStatistics($"{client.Framebuffer.Width}×{client.Framebuffer.Height}  ·  {_view.FramesPerSecond:0} fps  ·  {picture}  ·  {traffic}  ·  {string.Join(", ", features)}");
    }

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1 << 20 => $"{bytesPerSecond / (1 << 20):0.0} MB/s",
        >= 1 << 10 => $"{bytesPerSecond / (1 << 10):0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    /// <summary>The window closed: ends the connection, the agent and the stream.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusTimer.Stop();
        if (_clipboardListening) _clipboard.Changed -= OnLocalClipboardChangedAnyThread;
        _view.DetachSession();
        _player?.Dispose();
        _agent?.Dispose();
        Client?.Dispose();
        _audio.Dispose();
    }
}
