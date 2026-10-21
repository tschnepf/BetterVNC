using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.Protocol;
using BetterVnc.Viewer.Controls;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Core.Sessions;
using BetterVnc.Viewer.Input;
using BetterVnc.Viewer.Services;
using static BetterVnc.Viewer.Input.NativeMethods;

namespace BetterVnc.Viewer.Views;

/// <summary>
/// The window of one session: the toolbar, the picture, the overlay and the status bar, keyboard capture and fullscreen.
/// What the session does lives in its <see cref="SessionController"/>; this window is its <see cref="ISessionView"/>.
/// </summary>
public partial class SessionWindow : Window, ISessionView
{
    private readonly ConnectionProfile _profile = null!;
    private readonly SessionController _controller = null!;
    private readonly Win32Properties.CustomWndProcHookCallback _wndProc;
    private IntPtr _hwnd;
    private LowLevelKeyboardHook? _llHook;
    private bool _fullscreen;
    private WindowState _restoreState;
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _startFullscreen;
    private bool _pictureUpdating;
    private bool _screenshotToFile;

    /// <summary>The profile this window was opened for.</summary>
    public ConnectionProfile Profile => _profile;

    /// <summary>
    /// True while the VNC connection is up (false while connecting, after a disconnect, or on the error overlay); for a
    /// session without VNC, while the agent's stream shows.
    /// </summary>
    public bool IsConnected => _controller.IsConnected;

    /// <summary>For the XAML previewer only.</summary>
    public SessionWindow()
    {
        InitializeComponent();
        _wndProc = WndProc;
    }

    /// <param name="hostOverride">Resolved host/port/password for Unraid VMs (looked up over SSH before opening the window).</param>
    /// <param name="fullscreen">Start in fullscreen for this session only, without changing the profile's StartFullscreen setting.</param>
    /// <param name="guestAddress">An Unraid VM's own address, where its guest agent listens; null when not known yet.</param>
    /// <param name="profiles">The store <paramref name="profile"/> belongs to; <see cref="App.Profiles"/> when null.</param>
    public SessionWindow(ConnectionProfile profile, string? hostOverride = null, int? portOverride = null, string? passwordOverride = null,
        bool fullscreen = false, string? guestAddress = null, ProfileStore? profiles = null) : this()
    {
        _profile = profile;
        _startFullscreen = fullscreen || profile.StartFullscreen;
        _controller = new SessionController(profile, this, new SessionServices
        {
            Profiles = profiles ?? App.Profiles,
            Ui = AvaloniaUiThread.Instance,
            Clipboard = App.LocalClipboard,
            Audio = new AudioPlayer(),
            CreateJpegDecoder = () => new SkiaJpegDecoder(),
            JpegDecoderName = "SkiaSharp",
        }, hostOverride, portOverride, passwordOverride, guestAddress);
        _controller.ConnectionChanged += OnConnectionChanged;

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
        View.RenderedFrame += _controller.OnRenderedFrame;
        SendFilesButton.IsEnabled = PasteButton.IsEnabled = true;
        if (profile.AgentOnly)
        {
            CadButton.IsEnabled = false;
            ToolTip.SetTip(CadButton, "Ctrl+Alt+Del needs a VNC connection (or, later, the agent's system service): Windows ignores it from an app.");
            ToolTip.SetShowOnDisabled(CadButton, true);
            AudioToggle.IsEnabled = false;
        }

        _pictureUpdating = true;
        foreach (var info in PictureModes.All)
            PictureBox.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info, [ToolTip.TipProperty] = info.Description });
        PictureBox.SelectedIndex = Array.IndexOf(PictureModes.All, _controller.WantedPicture);
        _pictureUpdating = false;
        RefreshPictureMenu();

        _toolbarHideTimer.Tick += (_, _) =>
        {
            if (_fullscreen && !Toolbar.IsPointerOver && !PictureBox.IsDropDownOpen) ToolbarPopup.IsOpen = false;
            _toolbarHideTimer.Stop();
        };

        Opened += OnOpened;
        Closed += OnClosed;
        Activated += (_, _) => UpdateHook();
        Deactivated += (_, _) => { View.ReleaseAllKeys(); UpdateHook(); };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ScreenshotButton.AddHandler(PointerPressedEvent, (_, e) => _screenshotToFile = (e.KeyModifiers & KeyModifiers.Shift) != 0, RoutingStrategies.Tunnel);

        SessionManager.PlaceNewWindow(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Win32Properties.AddWndProcHookCallback(this, _wndProc);
        SessionManager.Register(this);
        if (_startFullscreen) FullscreenToggle.IsChecked = true;
        _ = _controller.ConnectAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SessionManager.Unregister(this);
        _toolbarHideTimer.Stop();
        ToolbarPopup.IsOpen = false;
        _llHook?.Dispose();
        Win32Properties.RemoveWndProcHookCallback(this, _wndProc);
        _controller.Dispose();
    }

    private void OnConnectionChanged()
    {
        SessionManager.NotifyStateChanged(this);
        UpdateHook();
    }

    /// <summary>Restores the window if it is minimised and brings it in front of the other windows.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _fullscreen ? WindowState.FullScreen : _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        // Activate alone only flashes the taskbar button when another window holds focus; a Topmost pulse forces it up.
        if (!Topmost) { Topmost = true; Topmost = false; }
        View.Focus();
    }

    // ------------------------------------------------------------------ ISessionView

    IVideoSurface ISessionView.Video => View;
    double ISessionView.FramesPerSecond => View.Fps;

    public void ShowOverlay(string title, string detail, bool showButtons)
    {
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        ReconnectButton.IsVisible = CloseButton.IsVisible = showButtons;
        Overlay.IsVisible = true;
        // The picture's native surface would cover the overlay: it steps aside, a still of the picture in its place.
        View.VideoSurfaceBlocked = true;
    }

    void ISessionView.HideOverlay()
    {
        Overlay.IsVisible = false;
        View.VideoSurfaceBlocked = false;
    }

    void ISessionView.SetStatus(string text) => StatusLeft.Text = text;
    void ISessionView.SetStatistics(string text) => StatusRight.Text = text;
    void ISessionView.SetTitle(string text) => TitleText.Text = text;

    void ISessionView.AttachSession(VncClient client)
    {
        View.Attach(client);
        View.Focus();
    }

    void ISessionView.AttachAgent(IAgentInput input) => View.AttachAgent(input);
    void ISessionView.SetAgentPictureSize(int width, int height) => View.SetAgentPictureSize(width, height);
    void ISessionView.FocusPicture() => View.Focus();
    void ISessionView.DetachSession() => View.Detach();
    void ISessionView.FitToRemote(int width, int height) => FitWindowToRemote(width, height);
    void ISessionView.SetScrollInjector(IScrollInjector? injector) => View.ScrollInjector = injector;
    void ISessionView.UseMacKeyboard() => View.Translator.MacMode = true;
    void ISessionView.ReleaseKeys() => View.ReleaseAllKeys();
    void ISessionView.SetAudioAvailable(bool available) => AudioToggle.IsEnabled = available;
    void ISessionView.Beep() => MessageBeep(0);

    VncCredentials? ISessionView.PromptCredentials(CredentialKind kind, string prompt, string? username, out bool remember)
    {
        var dlg = new CredentialsDialog(kind, prompt, username);
        remember = false;
        // The controller needs the answer now (a connection thread waits for it): a nested message loop, as WPF's ShowDialog.
        if (Modal.Run<bool>(dlg, this) != true) return null;
        remember = dlg.Remember;
        return new VncCredentials(dlg.Username, dlg.Password);
    }

    void ISessionView.ShowMessage(string title, string text, bool warning) =>
        _ = MessageDialog.ShowAsync(this, text, title, MessageButtons.Ok, warning ? MessageIcon.Warning : MessageIcon.Info);

    // ------------------------------------------------------------------ picture mode

    /// <summary>Enables each picture entry the current server and agent can deliver, with the reason on the disabled ones.</summary>
    public void RefreshPictureMenu()
    {
        foreach (var item in PictureBox.Items.OfType<ComboBoxItem>())
        {
            var info = (PictureModes.Info)item.Tag!;
            string? why = _controller.WhyUnavailable(info);
            item.IsEnabled = why == null;
            ToolTip.SetTip(item, why == null ? info.Description : $"{info.Description}\n\nNot available: {why}");
        }
        PictureDefaultItem.IsEnabled = _controller.CanMakePictureDefault;
    }

    private void OnPictureSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_pictureUpdating || PictureBox.SelectedItem is not ComboBoxItem { Tag: PictureModes.Info info }) return;
        _controller.SelectPicture(info);
        View.Focus();
    }

    private void OnPictureMakeDefault(object? sender, RoutedEventArgs e) => _controller.MakePictureDefault();

    /// <summary>Sizes the window so the remote screen shows 1:1 when it fits, otherwise fills most of the work area.</summary>
    private void FitWindowToRemote(int fbWidth, int fbHeight)
    {
        if (_fullscreen || WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double scale = RenderScaling;
        var work = screen.WorkingArea;
        double workW = work.Width / scale, workH = work.Height / scale;
        // The client area around the picture (toolbar, status bar) and the window frame around the client area.
        double chromeW = Bounds.Width - View.Bounds.Width, chromeH = Bounds.Height - View.Bounds.Height;
        double frameW = (FrameSize?.Width ?? Bounds.Width) - Bounds.Width, frameH = (FrameSize?.Height ?? Bounds.Height) - Bounds.Height;
        double wantW = fbWidth / scale + chromeW, wantH = fbHeight / scale + chromeH;
        Width = Math.Min(Math.Max(wantW, 780), workW * 0.95 - frameW);
        Height = Math.Min(wantH, workH * 0.95 - frameH);
        int outerW = (int)Math.Round((Width + frameW) * scale), outerH = (int)Math.Round((Height + frameH) * scale);
        Position = new PixelPoint(Math.Max(work.X, Math.Min(Position.X, work.Right - outerW)), Math.Max(work.Y, Math.Min(Position.Y, work.Bottom - outerH)));
    }

    private void OnReconnect(object? sender, RoutedEventArgs e) => _ = _controller.ConnectAsync();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        _controller.Disconnect();
        Close();
    }

    // ------------------------------------------------------------------ Win32 messages

    /// <summary>
    /// Raw key messages, before Avalonia sees them: while the picture has focus every key goes to the remote with its
    /// scan code, and Avalonia never handles it (so Alt and F10 do not open a menu).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch ((int)msg)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (View.IsAttached && View.IsFocused)
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
                if (View.IsFocused) handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private static bool IsCtrlShift() => (GetKeyState(VK_CONTROL) & 0x8000) != 0 && (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // F11 toggles fullscreen only while the viewer is not focused (otherwise F11 goes to the remote).
        if (e.Key == Key.F11 && !View.IsFocused)
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
                () => IsActive && View.IsFocused && GetForegroundWindow() == _hwnd,
                (vk, sc, ext, down) => Dispatcher.UIThread.Post(() => View.SendKey(vk, sc, ext, down)));
            _llHook.Install();
        }
        else
        {
            _llHook?.Uninstall();
        }
    }

    private void OnGrabToggled(object? sender, RoutedEventArgs e) => UpdateHook();

    // ------------------------------------------------------------------ clipboard and files

    private void OnClipboardToggled(object? sender, RoutedEventArgs e)
    {
        if (_controller != null) _controller.SyncClipboard = ClipboardToggle.IsChecked == true;
    }

    private async void OnPasteToRemote(object? sender, RoutedEventArgs e) => await _controller.PasteToRemoteAsync();

    private async void OnSendFiles(object? sender, RoutedEventArgs e)
    {
        if (!_controller.CanSendFiles)
        {
            await MessageDialog.ShowAsync(this, SessionController.NoFileChannelMessage, "Send files", MessageButtons.Ok, MessageIcon.Info);
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Send files to remote" });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        await _controller.UploadAsync(paths);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && _controller.CanSendFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is not { Count: > 0 }) return;
        e.Handled = true;
        await _controller.UploadAsync(paths);
    }

    // ------------------------------------------------------------------ toolbar actions

    private void OnCtrlAltDel(object? sender, RoutedEventArgs e)
    {
        View.SendCtrlAltDel();
        View.Focus();
    }

    private async void OnScreenshot(object? sender, RoutedEventArgs e)
    {
        bool toFile = _screenshotToFile;
        _screenshotToFile = false;
        if (View.Snapshot() is not { } shot) return;
        byte[] png = PngEncoder.Encode(shot.Bgra, shot.Width, shot.Height);
        if (toFile)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save screenshot",
                SuggestedFileName = $"{_profile.Name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } },
            });
            if (file != null)
            {
                await using (var stream = await file.OpenWriteAsync()) await stream.WriteAsync(png);
                StatusLeft.Text = $"Saved {file.TryGetLocalPath() ?? file.Name}";
            }
        }
        else
        {
            // PNG for apps that keep transparency, a bitmap for the ones that read only DIBs (the shared clipboard does both).
            await Task.Run(() => App.LocalClipboard.SetData(AgentFormat.Png, png));
            StatusLeft.Text = "Screenshot copied to clipboard";
        }
        View.Focus();
    }

    private void OnFitChanged(object? sender, RoutedEventArgs e) =>
        View.ScaleMode = FitToggle.IsChecked == true ? ScaleMode.Fit : ScaleMode.OneToOne;

    private void OnAudioToggled(object? sender, RoutedEventArgs e) => _controller?.SetAudio(AudioToggle.IsChecked == true);

    private void OnFullscreenToggled(object? sender, RoutedEventArgs e)
    {
        bool fs = FullscreenToggle.IsChecked == true;
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        if (fs)
        {
            _restoreState = WindowState;
            WindowState = WindowState.FullScreen;
            StatusBar.IsVisible = false;
            // The toolbar leaves the layout and floats over the picture in a popup window, shown from the top edge.
            ToolbarHost.Child = null;
            Toolbar.Opacity = 0.96;
            ToolbarPopup.Child = Toolbar;
        }
        else
        {
            ToolbarPopup.IsOpen = false;
            ToolbarPopup.Child = null;
            Toolbar.Opacity = 1;
            Toolbar.Width = double.NaN;
            ToolbarHost.Child = Toolbar;
            WindowState = _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            StatusBar.IsVisible = true;
        }
        UpdateHook();
        View.Focus();
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_fullscreen) return;
        var p = e.GetPosition(this);
        if (p.Y <= 2 && !ToolbarPopup.IsOpen)
        {
            Toolbar.Width = Bounds.Width;
            ToolbarPopup.IsOpen = true;
            _toolbarHideTimer.Start();
        }
        else if (ToolbarPopup.IsOpen && !Toolbar.IsPointerOver && p.Y > Toolbar.Bounds.Height + 10)
        {
            _toolbarHideTimer.Stop();
            _toolbarHideTimer.Start();
        }
    }
}
