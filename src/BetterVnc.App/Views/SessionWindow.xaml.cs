using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVnc.App.Input;
using BetterVnc.App.Models;
using BetterVnc.App.Services;
using BetterVnc.Protocol;
using Microsoft.Win32;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Views;

public partial class SessionWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _passwordOverride;
    private readonly string? _guestAddress;
    private VncClient? _client;
    private HwndSource? _hwndSource;
    private LowLevelKeyboardHook? _llHook;
    private readonly AudioPlayer _audio = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FileTransferService _files;
    private AgentClient? _agent;
    private string? _sessionUsername;
    private string? _sessionPassword;
    private string? _lastRemoteClipboard;
    private string? _lastLocalClipboard;
    private bool _audioRequested;
    private bool _clipboardListening;
    private bool _fullscreen;
    private WindowState _restoreState;
    private Rect _restoreBounds;
    private long _lastBytes;
    private DateTime _lastBytesTime = DateTime.UtcNow;
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _startFullscreen;
    private VideoStreamPlayer? _player;
    /// <summary>The picture mode chosen for this session (profile default until changed in the toolbar).</summary>
    private PictureModes.Info _wantedPicture;
    /// <summary>The Tight quality the RFB connection is currently asked for.</summary>
    private PictureModes.Info _activeVnc = PictureModes.StreamFallback;
    private bool _pictureUpdating;
    private long _jpegRectsAtQualityChange;

    /// <summary>The profile this window was opened for.</summary>
    public ConnectionProfile Profile => _profile;

    /// <summary>
    /// True while the VNC connection is up (false while connecting, after a disconnect, or on the error overlay); for a
    /// session without VNC, while the agent's stream shows.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>A session without VNC: the guest agent carries picture, keyboard, mouse and pointer shapes.</summary>
    private bool AgentOnly => _profile.AgentOnly;

    /// <summary>Something to send clipboard text and files to: the VNC connection, or the agent of a session without one.</summary>
    private bool HasSession => _client != null || (AgentOnly && _agent != null);

    /// <param name="hostOverride">Resolved host/port/password for Unraid VMs (looked up over SSH before opening the window).</param>
    /// <param name="fullscreen">Start in fullscreen for this session only, without changing the profile's StartFullscreen setting.</param>
    /// <param name="guestAddress">An Unraid VM's own address, where its guest agent listens; null when not known yet.</param>
    public SessionWindow(ConnectionProfile profile, string? hostOverride = null, int? portOverride = null, string? passwordOverride = null, bool fullscreen = false, string? guestAddress = null)
    {
        InitializeComponent();
        _profile = profile;
        _guestAddress = guestAddress;
        _startFullscreen = fullscreen || profile.StartFullscreen;
        // Profiles saved by older builds may still hold a "vnc://host" style value.
        var (parsedHost, parsedPort) = HostParser.Parse(hostOverride ?? profile.Host);
        _host = parsedHost;
        _port = portOverride ?? parsedPort ?? profile.Port;
        _passwordOverride = passwordOverride;
        _files = new FileTransferService(profile);

        Title = $"{profile.Name} - BetterVNC";
        TitleText.Text = profile.Name;
        FitToggle.IsChecked = profile.ScaleMode == ScaleMode.Fit;
        ClipboardToggle.IsChecked = profile.SyncClipboard;
        AudioToggle.IsChecked = profile.EnableAudio;
        GrabToggle.IsChecked = profile.CaptureWindowsKeys;
        View.ScaleMode = profile.ScaleMode;
        View.ScrollClicksPerNotch = profile.ScrollClicksPerNotch;
        View.Translator.MacMode = profile.Kind == ConnectionKind.Mac;
        View.Translator.MacModifiers = profile.MacModifiers;
        View.RenderedFrame += OnRenderedFrame;
        // The stream's GPU surface is a child window that WPF cannot draw over: it steps aside while the overlay shows.
        Overlay.IsVisibleChanged += (_, _) => View.VideoSurfaceBlocked = Overlay.IsVisible;
        SendFilesButton.IsEnabled = PasteButton.IsEnabled = true;

        _wantedPicture = PictureModes.Get(profile.PictureMode);
        // Without VNC the picture is always the stream; a VNC mode in the profile means its best stream.
        if (profile.AgentOnly && !_wantedPicture.IsStream) _wantedPicture = PictureModes.Get(PictureMode.StreamQuality);
        if (profile.AgentOnly)
        {
            CadButton.IsEnabled = false;
            CadButton.ToolTip = "Ctrl+Alt+Del needs a VNC connection (or, later, the agent's system service): Windows ignores it from an app.";
            AudioToggle.IsEnabled = false;
        }
        _pictureUpdating = true;
        foreach (var info in PictureModes.All)
            PictureBox.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info, ToolTip = info.Description });
        PictureBox.SelectedIndex = Array.IndexOf(PictureModes.All, _wantedPicture);
        _pictureUpdating = false;
        RefreshPictureMenu();

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _toolbarHideTimer.Tick += (_, _) => { if (_fullscreen && !Toolbar.IsMouseOver) Toolbar.Visibility = Visibility.Collapsed; _toolbarHideTimer.Stop(); };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Activated += (_, _) => UpdateHook();
        Deactivated += (_, _) => { View.ReleaseAllKeys(); UpdateHook(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && CanSendFiles ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        MouseMove += OnWindowMouseMove;

        SessionManager.PlaceNewWindow(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        SessionManager.Register(this);
        if (_startFullscreen) FullscreenToggle.IsChecked = true;
        _ = ConnectAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SessionManager.Unregister(this);
        _statusTimer.Stop();
        _streamRetryTimer?.Stop();
        _llHook?.Dispose();
        if (_clipboardListening && _hwndSource != null) RemoveClipboardFormatListener(_hwndSource.Handle);
        _hwndSource?.RemoveHook(WndProc);
        View.Detach();
        _player?.Dispose();
        _agent?.Dispose();
        _client?.Dispose();
        _audio.Dispose();
    }

    /// <summary>Files can go through the guest agent (preferred) or the SFTP/SMB drop folder.</summary>
    private bool CanSendFiles => _agent?.IsConnected == true || _files.IsConfigured;

    /// <summary>Restores the window if it is minimised and brings it in front of the other windows.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _fullscreen || _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        // Activate alone only flashes the taskbar button when another window holds focus; a Topmost pulse forces it up.
        if (!Topmost) { Topmost = true; Topmost = false; }
        View.Focus();
    }

    // ------------------------------------------------------------------ connection

    private async Task ConnectAsync()
    {
        if (AgentOnly)
        {
            ConnectThroughAgent();
            return;
        }
        ShowOverlay("Connecting…", $"{_host}:{_port}", showButtons: false);
        View.Detach();
        View.ScrollInjector = null;
        _player?.Dispose();
        _player = null;
        _agent?.Dispose();
        _agent = null;
        _client?.Dispose();
        _audioRequested = false;

        var password = _sessionPassword ?? _passwordOverride ?? ProfileStore.Unprotect(_profile.ProtectedPassword);
        // A stream mode starts out on lossless VNC; the stream takes over once the agent is up (see ApplyPictureAsync).
        var initialVnc = _wantedPicture.IsStream ? PictureModes.StreamFallback : _wantedPicture;
        var options = new VncConnectionOptions
        {
            Host = _host,
            Port = _port,
            Username = _sessionUsername ?? _profile.Username,
            Password = password,
            CompressionLevel = initialVnc.CompressionLevel,
            JpegQuality = initialVnc.JpegQuality,
            EnableLocalCursor = _profile.LocalCursor,
            EnableQemuAudio = true,
            JpegDecoder = new WicJpegDecoder(),
            CredentialProvider = (kind, prompt) => Dispatcher.Invoke(() => PromptCredentials(kind, prompt)),
        };
        var client = new VncClient(options);
        client.Log += m => Dispatcher.BeginInvoke(() => StatusLeft.Text = m);
        client.Disconnected += ex => Dispatcher.BeginInvoke(() => OnDisconnected(ex));
        client.ClipboardTextReceived += t => Dispatcher.BeginInvoke(() => OnRemoteClipboard(t));
        client.ClipboardRequested += () => Dispatcher.BeginInvoke(PushLocalClipboard);
        client.Bell += () => Dispatcher.BeginInvoke(() => System.Media.SystemSounds.Beep.Play());
        client.DesktopNameChanged += n => Dispatcher.BeginInvoke(() => TitleText.Text = $"{_profile.Name}  ·  {n}");
        client.AudioBegin += (fmt, ch, hz) => Dispatcher.BeginInvoke(() => _audio.Start(hz, ch, 16));
        client.AudioData += data => _audio.Feed(data);
        client.AudioEnd += () => Dispatcher.BeginInvoke(() => _audio.Stop());

        try
        {
            await client.ConnectAsync();
        }
        catch (VncAuthenticationException ex)
        {
            bool apple = client.Capabilities.SecurityTypeUsed == SecurityType.AppleArd;
            client.Dispose();
            ShowOverlay("Authentication failed", ex.Message, showButtons: true);
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
            ShowOverlay("Could not connect", ex.Message, showButtons: true);
            return;
        }

        _client = client;
        _activeVnc = initialVnc;
        _jpegRectsAtQualityChange = 0;
        IsConnected = true;
        SessionManager.NotifyStateChanged(this);
        if (client.Capabilities.IsApple && _profile.Kind != ConnectionKind.Mac)
            View.Translator.MacMode = true;
        View.Attach(client);
        View.Focus();
        Keyboard.Focus(View);
        Overlay.Visibility = Visibility.Collapsed;
        FitWindowToRemote(client.Framebuffer.Width, client.Framebuffer.Height);
        TitleText.Text = $"{_profile.Name}  ·  {client.DesktopName}";
        StatusLeft.Text = $"Connected  ·  {client.Capabilities}";
        _profile.LastConnected = DateTime.Now;
        try { App.Profiles.Save(); } catch { }

        if (!_clipboardListening && _hwndSource != null)
        {
            AddClipboardFormatListener(_hwndSource.Handle);
            _clipboardListening = true;
        }
        _statusTimer.Start();
        UpdateHook();
        StartAgent();
        RefreshPictureMenu();
        UpdateStatus();
        if (_wantedPicture.IsStream && _agent == null)
            StatusLeft.Text = "Stream mode needs the guest agent; showing VNC lossless because this connection has none configured";
    }

    // ------------------------------------------------------------------ session without VNC

    private bool _fittedToStream;
    private DispatcherTimer? _streamRetryTimer;

    /// <summary>
    /// A session without VNC: the guest agent is the whole connection. Its client reconnects by itself; the stream starts
    /// whenever the agent is up (and again after it comes back), and the overlay shows while there is no picture.
    /// </summary>
    private void ConnectThroughAgent()
    {
        _streamRetryTimer?.Stop();
        View.Detach();
        View.ScrollInjector = null;
        _player?.Dispose();
        _player = null;
        _agent?.Dispose();
        _agent = null;
        SetAgentSessionUp(false);
        if (!AgentClient.IsConfigured(_profile))
        {
            ShowOverlay("Cannot connect", "This connection goes through the guest agent alone, but no agent is set up in it. Edit the connection: turn the agent on and choose pairing or enter its token.", showButtons: true);
            return;
        }
        var agent = new AgentClient(_profile, _host, Dispatcher, _guestAddress, GuestAddressLookUp());
        ShowOverlay("Connecting…", $"Guest agent at {agent.Target}", showButtons: false);
        agent.StateChanged += () => { if (_agent == agent) OnAgentOnlyStateChanged(agent); };
        agent.Message += m =>
        {
            if (_agent != agent) return;
            StatusLeft.Text = m;
            if (!IsConnected && Overlay.IsVisible && OverlayTitle.Text != "Cannot control this computer") OverlayDetail.Text = m;
        };
        agent.RemoteTextReceived += t => { if (_agent == agent) OnRemoteClipboard(t); };
        agent.CursorChanged += (shape, visible) =>
        {
            if (_agent != agent) return;
            if (shape != null) View.SetRemoteCursor(shape);
            View.SetRemoteCursorVisible(visible);
        };
        _agent = agent;
        View.AttachAgent(agent);
        View.ScrollInjector = agent;
        var player = new VideoStreamPlayer(agent, null, View, Dispatcher);
        player.Message += m => { if (_player == player) StatusLeft.Text = m; };
        player.Ended += reason => { if (_player == player) OnAgentOnlyStreamEnded(agent, reason); };
        _player = player;
        if (!_clipboardListening && _hwndSource != null)
        {
            AddClipboardFormatListener(_hwndSource.Handle);
            _clipboardListening = true;
        }
        _statusTimer.Start();
        agent.Start();
        RefreshPictureMenu();
    }

    private void OnAgentOnlyStateChanged(AgentClient agent)
    {
        RefreshPictureMenu();
        UpdateStatus();
        if (agent.IsConnected)
        {
            if (!agent.CanControl)
            {
                SetAgentSessionUp(false);
                ShowOverlay("Cannot control this computer", agent.ControlUnavailableReason ?? "The guest agent cannot be controlled.", showButtons: true);
                return;
            }
            if (_player is { IsStreaming: false }) _ = StartAgentStreamAsync();
            return;
        }
        if (IsConnected)
        {
            SetAgentSessionUp(false);
            ShowOverlay("Connection lost", $"Reconnecting to the guest agent at {agent.Target}…", showButtons: true);
        }
        else if (Overlay.IsVisible)
        {
            // Still trying (or given up: pairing denied, token rejected); the agent's own message has the details.
            ReconnectButton.Visibility = CloseButton.Visibility = Visibility.Visible;
        }
    }

    private async Task StartAgentStreamAsync()
    {
        var agent = _agent;
        var player = _player;
        if (agent == null || player == null || player.IsStreaming) return;
        var mode = _wantedPicture.IsStream ? _wantedPicture : PictureModes.Get(PictureMode.StreamQuality);
        try
        {
            await player.StartAsync(mode);
        }
        catch (Exception ex)
        {
            if (_agent != agent) return;
            ShowOverlay("No picture", "The guest agent could not start streaming: " + ex.Message, showButtons: true);
            ScheduleStreamRetry();
            return;
        }
        if (_agent != agent || agent.StreamInfo is not { } info) return;
        View.SetAgentPictureSize(info.Width, info.Height);
        if (!_fittedToStream)
        {
            _fittedToStream = true;
            FitWindowToRemote(info.Width, info.Height);
        }
        SetAgentSessionUp(true);
        TitleText.Text = $"{_profile.Name}  ·  {agent.RemoteOs} through the guest agent";
        _profile.LastConnected = DateTime.Now;
        try { App.Profiles.Save(); } catch { }
        RefreshPictureMenu();
        UpdateStatus();
    }

    /// <summary>The agent stopped the stream on its own (capture failed, desktop locked): try again shortly while it stays connected.</summary>
    private void OnAgentOnlyStreamEnded(AgentClient agent, string? reason)
    {
        RefreshPictureMenu();
        UpdateStatus();
        if (!agent.IsConnected) return; // the state change shows "connection lost" and restarts the stream on reconnect
        SetAgentSessionUp(false);
        ShowOverlay("No picture", $"The remote stopped streaming ({reason ?? "no reason given"}). Trying again…", showButtons: true);
        ScheduleStreamRetry();
    }

    private void ScheduleStreamRetry()
    {
        _streamRetryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _streamRetryTimer.Tick -= OnStreamRetry;
        _streamRetryTimer.Tick += OnStreamRetry;
        _streamRetryTimer.Stop();
        _streamRetryTimer.Start();
    }

    private void OnStreamRetry(object? sender, EventArgs e)
    {
        _streamRetryTimer?.Stop();
        if (_agent is { CanControl: true } && _player is { IsStreaming: false }) _ = StartAgentStreamAsync();
    }

    /// <summary>The picture of a session without VNC appeared (the stream started) or went away.</summary>
    private void SetAgentSessionUp(bool up)
    {
        if (up)
        {
            Overlay.Visibility = Visibility.Collapsed;
            View.Focus();
            Keyboard.Focus(View);
        }
        else
        {
            View.ReleaseAllKeys();
        }
        if (IsConnected == up) return;
        IsConnected = up;
        SessionManager.NotifyStateChanged(this);
        UpdateHook();
    }

    // ------------------------------------------------------------------ guest agent

    /// <summary>Connects to the BetterVNC guest agent (rich clipboard, scrolling, screen stream) once the VNC session is up, if the profile has one.</summary>
    private void StartAgent()
    {
        if (!AgentClient.IsConfigured(_profile) || _client == null) return;
        var agent = new AgentClient(_profile, _host, Dispatcher, _guestAddress, GuestAddressLookUp());
        agent.StateChanged += () => { if (_agent == agent) OnAgentStateChanged(agent); };
        agent.Message += m => { if (_agent == agent) StatusLeft.Text = m; };
        agent.RemoteTextReceived += t => { if (_agent == agent) OnRemoteClipboard(t); };
        _agent = agent;
        View.ScrollInjector = agent;
        var player = new VideoStreamPlayer(agent, _client, View, Dispatcher);
        player.Message += m => { if (_player == player) StatusLeft.Text = m; };
        player.Ended += _ => { if (_player == player) { RefreshPictureMenu(); UpdateStatus(); } };
        _player = player;
        StatusLeft.Text = $"Connecting to guest agent at {agent.Target}…";
        agent.Start();
    }

    /// <summary>
    /// For an Unraid VM the VNC host is the Unraid server, so the agent's address (the VM's own) is looked up there,
    /// again whenever it is unknown or stops answering. Null for every other kind of connection.
    /// </summary>
    private Func<CancellationToken, Task<string?>>? GuestAddressLookUp()
    {
        if (_profile.Kind != ConnectionKind.UnraidVm || _profile.UnraidVmName is not { Length: > 0 } vmName
            || App.Profiles.FindServer(_profile.UnraidServerId) is not { } server)
            return null;
        return ct => new UnraidService(server).GetGuestAddressAsync(vmName, ct);
    }

    private void OnAgentStateChanged(AgentClient agent)
    {
        RefreshPictureMenu();
        UpdateStatus();
        // The agent came up (or back) and the person wants a stream: start it now, the same way clipboard features light up.
        if (_wantedPicture.IsStream && agent.CanStream && _player is { IsStreaming: false })
            _ = ApplyPictureAsync();
    }

    // ------------------------------------------------------------------ picture mode

    /// <summary>Enables each picture entry the current server and agent can deliver, with the reason on the disabled ones.</summary>
    private void RefreshPictureMenu()
    {
        var client = _client;
        var caps = client?.Capabilities;
        foreach (ComboBoxItem item in PictureBox.Items)
        {
            var info = (PictureModes.Info)item.Tag;
            string? why = null;
            if (!info.IsStream && AgentOnly)
            {
                why = "This connection has no VNC server: the picture is the guest agent's stream.";
            }
            else if (info.IsStream)
            {
                if (_agent == null || _player == null)
                    why = "This connection has no guest agent. Enable the agent in the connection settings; the stream modes need it.";
                else if (!_agent.CanStream)
                    why = _agent.StreamUnavailableReason;
            }
            else if (info.IsLossy && caps is { IsApple: true })
            {
                why = "macOS Screen Sharing offers no lossy encoding (Raw, Hextile, Zlib, ZRLE only).";
            }
            item.IsEnabled = why == null;
            item.ToolTip = why == null ? info.Description : $"{info.Description}\n\nNot available: {why}";
        }
        PictureDefaultItem.IsEnabled = _wantedPicture.Mode != _profile.PictureMode;
    }

    private void OnPictureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_pictureUpdating || PictureBox.SelectedItem is not ComboBoxItem { Tag: PictureModes.Info info }) return;
        _wantedPicture = info;
        _ = ApplyPictureAsync();
        View.Focus();
    }

    private void OnPictureMakeDefault(object sender, RoutedEventArgs e)
    {
        _profile.PictureMode = _wantedPicture.Mode;
        var vnc = _wantedPicture.IsStream ? PictureModes.StreamFallback : _wantedPicture;
        _profile.JpegQuality = vnc.JpegQuality;
        _profile.CompressionLevel = vnc.CompressionLevel;
        try { App.Profiles.Save(); } catch { }
        RefreshPictureMenu();
        StatusLeft.Text = $"\"{_wantedPicture.Label}\" is now the default picture for {_profile.Name}";
    }

    /// <summary>
    /// Makes the picture match <see cref="_wantedPicture"/>: a VNC mode changes the Tight encodings on the live connection; a
    /// stream mode starts (or re-tunes) the agent stream, or falls back to lossless VNC while the agent cannot stream.
    /// </summary>
    private async Task ApplyPictureAsync()
    {
        if (AgentOnly)
        {
            // Only stream modes can be chosen here: re-tune the running stream, or start it if the agent is up without one.
            if (!_wantedPicture.IsStream || _player == null || _agent?.CanControl != true) return;
            try
            {
                if (_player.IsStreaming) await _player.StartAsync(_wantedPicture);
                else await StartAgentStreamAsync();
            }
            catch (Exception ex) { StatusLeft.Text = "Picture: " + ex.Message; }
            RefreshPictureMenu();
            UpdateStatus();
            return;
        }
        var client = _client;
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
                    StatusLeft.Text = _agent == null
                        ? "Stream mode needs the guest agent; showing VNC lossless because this connection has none configured"
                        : $"Showing VNC lossless until the guest agent can stream: {_agent.StreamUnavailableReason}";
                }
            }
            else
            {
                if (_player?.IsStreaming == true) await _player.StopAsync();
                SetVncQuality(client, mode);
                StatusLeft.Text = mode.IsLossy
                    ? $"Picture: {mode.Label} (Tight JPEG {mode.JpegQuality}). If the status bar never shows JPEG, the server ignores lossy requests (QEMU needs lossy=on)."
                    : $"Picture: {mode.Label}";
            }
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Picture: " + ex.Message;
        }
        RefreshPictureMenu();
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

    /// <summary>Sizes the window so the remote screen shows 1:1 when it fits, otherwise fills most of the work area.</summary>
    private void FitWindowToRemote(int fbWidth, int fbHeight)
    {
        if (_fullscreen || WindowState != WindowState.Normal) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea;
        double chromeW = ActualWidth - Scroller.ActualWidth;
        double chromeH = ActualHeight - Scroller.ActualHeight;
        double wantW = fbWidth / dpi.DpiScaleX + chromeW;
        double wantH = fbHeight / dpi.DpiScaleY + chromeH;
        Width = Math.Min(Math.Max(wantW, 780), work.Width * 0.95);
        Height = Math.Min(wantH, work.Height * 0.95);
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
    }

    private VncCredentials? PromptCredentials(CredentialKind kind, string prompt)
    {
        var dlg = new CredentialsDialog(kind, prompt, _profile.Username) { Owner = this };
        if (dlg.ShowDialog() != true) return null;
        if (dlg.Remember)
        {
            if (kind == CredentialKind.UsernameAndPassword) _profile.Username = dlg.Username;
            _profile.ProtectedPassword = ProfileStore.Protect(dlg.Password);
            try { App.Profiles.Save(); } catch { }
        }
        return new VncCredentials(dlg.Username, dlg.Password);
    }

    private void OnDisconnected(Exception? ex)
    {
        IsConnected = false;
        SessionManager.NotifyStateChanged(this);
        _statusTimer.Stop();
        _audio.Stop();
        View.ScrollInjector = null;
        _player?.Dispose();
        _player = null;
        _agent?.Dispose();
        _agent = null;
        RefreshPictureMenu();
        View.ReleaseAllKeys();
        UpdateHook();
        if (ex == null)
            ShowOverlay("Disconnected", "The session ended.", showButtons: true);
        else
            ShowOverlay("Connection lost", ex.Message, showButtons: true);
    }

    private void ShowOverlay(string title, string detail, bool showButtons)
    {
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        ReconnectButton.Visibility = CloseButton.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        _client?.Disconnect();
        Close();
    }

    // ------------------------------------------------------------------ Win32 messages

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (View.IsAttached && View.IsKeyboardFocused)
                {
                    int vk = (int)wParam;
                    long lp = lParam.ToInt64();
                    int scancode = (int)((lp >> 16) & 0xFF);
                    bool extended = ((lp >> 24) & 1) != 0;
                    bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                    if (vk == 0x7A && down && !extended && IsCtrlShift()) // Ctrl+Shift+F11: toggle fullscreen without sending it
                    {
                        FullscreenToggle.IsChecked = !_fullscreen;
                        handled = true;
                        break;
                    }
                    View.SendKey(vk, scancode, extended, down);
                    handled = true;
                }
                break;
            case WM_SYSCHAR:
            case WM_CHAR:
                if (View.IsKeyboardFocused) handled = true;
                break;
            case WM_MOUSEHWHEEL:
                if (View.IsAttached)
                {
                    int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    View.SendWheel(delta, horizontal: true, Mouse.GetPosition(View));
                    handled = true;
                }
                break;
            case WM_CLIPBOARDUPDATE:
                OnLocalClipboardChanged();
                break;
        }
        return IntPtr.Zero;
    }

    private static bool IsCtrlShift() => (GetKeyState(VK_CONTROL) & 0x8000) != 0 && (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F11 toggles fullscreen only while the viewer is not focused (otherwise F11 goes to the remote).
        if (e.Key == Key.F11 && !View.IsKeyboardFocused)
        {
            FullscreenToggle.IsChecked = !_fullscreen;
            e.Handled = true;
        }
    }

    private void UpdateHook()
    {
        bool wantHook = View.IsAttached && IsActive && (GrabToggle.IsChecked == true || _fullscreen);
        if (wantHook)
        {
            _llHook ??= new LowLevelKeyboardHook(
                () => IsActive && View.IsKeyboardFocused && GetForegroundWindow() == _hwndSource?.Handle,
                (vk, sc, ext, down) => Dispatcher.BeginInvoke(() => View.SendKey(vk, sc, ext, down)));
            _llHook.Install();
        }
        else
        {
            _llHook?.Uninstall();
        }
    }

    // ------------------------------------------------------------------ clipboard

    private void OnLocalClipboardChanged()
    {
        if (!HasSession || ClipboardToggle.IsChecked != true) return;
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
                    hasText = Clipboard.ContainsText();
                    hasFiles = Clipboard.ContainsFileDropList();
                    hasImage = Clipboard.ContainsImage() || Clipboard.ContainsData("PNG");
                    break;
                }
                catch (System.Runtime.InteropServices.COMException) { await Task.Delay(30); }
            }
            // Text goes through PushLocalClipboard; a copy that carries text and a picture (Excel, Word) is almost always meant as text.
            if (!hasImage || hasText || hasFiles) return;
            await agent.PushClipboardImageAsync();
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Guest clipboard: " + ex.Message;
        }
    }

    private void PushLocalClipboard()
    {
        if (!HasSession) return;
        string? text = null;
        for (int attempt = 0; attempt < 3 && text == null; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                else return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        if (string.IsNullOrEmpty(text) || text == _lastRemoteClipboard || text == _lastLocalClipboard) return;
        _lastLocalClipboard = text;
        // Through the agent when it is connected: UTF-8, and it works where the VNC server keeps no clipboard of its own
        // (QEMU without a qemu-vdagent channel, as on most Unraid VMs). Over VNC otherwise.
        if (_agent is { IsConnected: true } agent) _ = PushTextThroughAgentAsync(agent, _client, text);
        else _client?.SendClipboardText(text);
    }

    private static async Task PushTextThroughAgentAsync(AgentClient agent, VncClient? client, string text)
    {
        bool sent;
        try { sent = await agent.PushTextAsync(text); }
        catch (Exception) { sent = false; }
        if (!sent) client?.SendClipboardText(text);
    }

    private void OnRemoteClipboard(string text)
    {
        if (ClipboardToggle.IsChecked != true || string.IsNullOrEmpty(text) || text == _lastRemoteClipboard) return;
        _lastRemoteClipboard = text;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                StatusLeft.Text = "Clipboard received from remote";
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
    }

    private async void OnPasteToRemote(object sender, RoutedEventArgs e)
    {
        if (!HasSession) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                await UploadAsync(files);
            }
            else if (Clipboard.ContainsImage())
            {
                if (_agent?.IsConnected == true)
                {
                    if (await _agent.PushClipboardImageAsync()) return;
                }
                var image = Clipboard.GetImage();
                if (image == null) return;
                var tmp = Path.Combine(Path.GetTempPath(), $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                SavePng(image, tmp);
                await UploadAsync(new[] { tmp });
                try { File.Delete(tmp); } catch { }
            }
            else if (Clipboard.ContainsText())
            {
                _lastLocalClipboard = null;
                PushLocalClipboard();
                StatusLeft.Text = "Clipboard text sent to remote";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Paste to remote", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ files

    private async void OnSendFiles(object sender, RoutedEventArgs e)
    {
        if (!CanSendFiles)
        {
            MessageBox.Show(this, NoFileChannelMessage, "Send files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Send files to remote" };
        if (dlg.ShowDialog(this) != true) return;
        await UploadAsync(dlg.FileNames);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await UploadAsync(files);
    }

    private const string NoFileChannelMessage =
        "VNC itself cannot carry files. Either run the BetterVNC guest agent in the remote machine and enable it in the connection " +
        "(files land directly on the remote clipboard), or configure an SFTP/SMB drop folder.";

    private async Task UploadAsync(IReadOnlyList<string> files)
    {
        if (_agent?.IsConnected == true)
        {
            try
            {
                await _agent.PushFilesAsync(files, new Progress<string>(m => StatusLeft.Text = m));
                StatusLeft.Text = $"{files.Count} item(s) placed on the guest clipboard; paste them in the remote";
            }
            catch (Exception ex)
            {
                StatusLeft.Text = "Send failed";
                MessageBox.Show(this, ex.Message, "Send files to guest", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        if (!_files.IsConfigured)
        {
            MessageBox.Show(this, NoFileChannelMessage, "Send files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var progress = new Progress<(string file, double fraction)>(p => StatusLeft.Text = $"Uploading {p.file}  {p.fraction:P0}");
        try
        {
            var written = await _files.UploadAsync(files, progress, CancellationToken.None);
            StatusLeft.Text = $"Uploaded {written.Count} file(s) via {_files.Description}";
            // Put the remote path on the remote clipboard so it can be pasted straight away.
            if (written.Count > 0 && _client != null && ClipboardToggle.IsChecked == true)
                _client.SendClipboardText(string.Join("\n", written));
        }
        catch (Exception ex)
        {
            StatusLeft.Text = "Upload failed";
            MessageBox.Show(this, ex.Message, "Upload failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ toolbar actions

    private void OnCtrlAltDel(object sender, RoutedEventArgs e)
    {
        View.SendCtrlAltDel();
        View.Focus();
    }

    private void OnScreenshot(object sender, RoutedEventArgs e)
    {
        var shot = View.Snapshot();
        if (shot == null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"{_profile.Name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png" };
            if (dlg.ShowDialog(this) == true)
            {
                SavePng(shot, dlg.FileName);
                StatusLeft.Text = $"Saved {dlg.FileName}";
            }
        }
        else
        {
            Clipboard.SetImage(shot);
            StatusLeft.Text = "Screenshot copied to clipboard";
        }
        View.Focus();
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        View.ScaleMode = FitToggle.IsChecked == true ? ScaleMode.Fit : ScaleMode.OneToOne;
        Scroller.HorizontalScrollBarVisibility = Scroller.VerticalScrollBarVisibility =
            View.ScaleMode == ScaleMode.Fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnAudioToggled(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        if (AudioToggle.IsChecked == true) { _client.SetAudioEnabled(true); _audioRequested = true; }
        else { _client.SetAudioEnabled(false); _audio.Stop(); }
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        bool fs = FullscreenToggle.IsChecked == true;
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        if (fs)
        {
            _restoreState = WindowState;
            _restoreBounds = RestoreBounds;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // force a refresh so the chrome disappears
            WindowState = WindowState.Maximized;
            StatusBar.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            Toolbar.Opacity = 0.96;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            StatusBar.Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Visible;
            Toolbar.Opacity = 1;
        }
        UpdateHook();
        View.Focus();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        var p = e.GetPosition(this);
        if (p.Y <= 2 && Toolbar.Visibility != Visibility.Visible)
        {
            Toolbar.Visibility = Visibility.Visible;
            _toolbarHideTimer.Start();
        }
        else if (Toolbar.Visibility == Visibility.Visible && !Toolbar.IsMouseOver && p.Y > Toolbar.ActualHeight + 10)
        {
            _toolbarHideTimer.Stop();
            _toolbarHideTimer.Start();
        }
    }

    // ------------------------------------------------------------------ status

    private void OnRenderedFrame()
    {
        var client = _client;
        if (client == null) return;
        if (!_audioRequested && client.Capabilities.QemuAudio && AudioToggle.IsChecked == true)
        {
            _audioRequested = true;
            client.SetAudioEnabled(true);
        }
        if (!AudioToggle.IsEnabled && client.Capabilities.QemuAudio) AudioToggle.IsEnabled = true;
    }

    private void UpdateStatus()
    {
        if (AgentOnly)
        {
            UpdateAgentOnlyStatus();
            return;
        }
        var client = _client;
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
        AudioToggle.IsEnabled = caps.QemuAudio;

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

        StatusRight.Text = $"{client.Framebuffer.Width}×{client.Framebuffer.Height}  ·  {View.Fps:0} fps  ·  {picture}  ·  {traffic}  ·  {string.Join(", ", features)}";
    }

    /// <summary>The status bar of a session without VNC: the stream, its traffic and what the agent does.</summary>
    private void UpdateAgentOnlyStatus()
    {
        var agent = _agent;
        if (agent == null) return;
        var features = new List<string> { agent.IsConnected ? $"agent ({agent.RemoteOs})" : agent.Status };
        if (agent.IsConnected)
        {
            if (agent.CanControl) features.Add("keyboard and mouse");
            if (agent.StreamInfo is { Cursor: true }) features.Add("pointer shapes");
            if (agent.CanScroll) features.Add("smooth scroll");
        }
        if (_player is { IsStreaming: true } player && player.Info is { } info)
        {
            string path = player.DecodePath is { } p ? $" ({p})" : "";
            string skipped = player.FramesSkipped > 0 ? $" ({player.FramesSkipped} skipped)" : "";
            StatusRight.Text = $"{info.Width}×{info.Height}  ·  {View.Fps:0} fps  ·  stream {info.Codec}, {info.EncoderKind} encoder{path}{skipped}  ·  {FormatRate(player.TakeRate())}  ·  {string.Join(", ", features)}";
        }
        else
        {
            StatusRight.Text = $"no picture yet  ·  {string.Join(", ", features)}";
        }
    }

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1 << 20 => $"{bytesPerSecond / (1 << 20):0.0} MB/s",
        >= 1 << 10 => $"{bytesPerSecond / (1 << 10):0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };
}
