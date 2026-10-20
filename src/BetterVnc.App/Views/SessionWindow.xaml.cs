using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVnc.App.Input;
using BetterVnc.App.Services;
using BetterVnc.Protocol;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Core.Sessions;
using Microsoft.Win32;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Views;

/// <summary>
/// The window of one session: the toolbar, the picture, the overlay and the status bar, keyboard capture and fullscreen.
/// What the session does lives in its <see cref="SessionController"/>; this window is its <see cref="ISessionView"/>.
/// </summary>
public partial class SessionWindow : Window, ISessionView
{
    private readonly ConnectionProfile _profile;
    private readonly SessionController _controller;
    private HwndSource? _hwndSource;
    private LowLevelKeyboardHook? _llHook;
    private bool _fullscreen;
    private WindowState _restoreState;
    private Rect _restoreBounds;
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _startFullscreen;
    private bool _pictureUpdating;

    /// <summary>The profile this window was opened for.</summary>
    public ConnectionProfile Profile => _profile;

    /// <summary>
    /// True while the VNC connection is up (false while connecting, after a disconnect, or on the error overlay); for a
    /// session without VNC, while the agent's stream shows.
    /// </summary>
    public bool IsConnected => _controller.IsConnected;

    /// <param name="hostOverride">Resolved host/port/password for Unraid VMs (looked up over SSH before opening the window).</param>
    /// <param name="fullscreen">Start in fullscreen for this session only, without changing the profile's StartFullscreen setting.</param>
    /// <param name="guestAddress">An Unraid VM's own address, where its guest agent listens; null when not known yet.</param>
    /// <param name="profiles">The store <paramref name="profile"/> belongs to; <see cref="App.Profiles"/> when null.</param>
    public SessionWindow(ConnectionProfile profile, string? hostOverride = null, int? portOverride = null, string? passwordOverride = null,
        bool fullscreen = false, string? guestAddress = null, ProfileStore? profiles = null)
    {
        InitializeComponent();
        _profile = profile;
        _startFullscreen = fullscreen || profile.StartFullscreen;
        _controller = new SessionController(profile, this, new SessionServices
        {
            Profiles = profiles ?? App.Profiles,
            Ui = new WpfUiThread(Dispatcher),
            Clipboard = App.LocalClipboard,
            Audio = new AudioPlayer(),
            CreateJpegDecoder = () => new WicJpegDecoder(),
            JpegDecoderName = "WIC",
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
        // The stream's GPU surface is a child window that WPF cannot draw over: it steps aside while the overlay shows.
        Overlay.IsVisibleChanged += (_, _) => View.VideoSurfaceBlocked = Overlay.IsVisible;
        SendFilesButton.IsEnabled = PasteButton.IsEnabled = true;
        if (profile.AgentOnly)
        {
            CadButton.IsEnabled = false;
            CadButton.ToolTip = "Ctrl+Alt+Del needs a VNC connection (or, later, the agent's system service): Windows ignores it from an app.";
            AudioToggle.IsEnabled = false;
        }

        _pictureUpdating = true;
        foreach (var info in PictureModes.All)
            PictureBox.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info, ToolTip = info.Description });
        PictureBox.SelectedIndex = Array.IndexOf(PictureModes.All, _controller.WantedPicture);
        _pictureUpdating = false;
        RefreshPictureMenu();

        _toolbarHideTimer.Tick += (_, _) => { if (_fullscreen && !Toolbar.IsMouseOver) Toolbar.Visibility = Visibility.Collapsed; _toolbarHideTimer.Stop(); };

        Loaded += OnLoaded;
        Closed += OnClosed;
        Activated += (_, _) => UpdateHook();
        Deactivated += (_, _) => { View.ReleaseAllKeys(); UpdateHook(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _controller.CanSendFiles ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        MouseMove += OnWindowMouseMove;

        SessionManager.PlaceNewWindow(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        SessionManager.Register(this);
        if (_startFullscreen) FullscreenToggle.IsChecked = true;
        _ = _controller.ConnectAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SessionManager.Unregister(this);
        _llHook?.Dispose();
        _hwndSource?.RemoveHook(WndProc);
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
            WindowState = _fullscreen || _restoreState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
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
        ReconnectButton.Visibility = CloseButton.Visibility = showButtons ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    void ISessionView.HideOverlay() => Overlay.Visibility = Visibility.Collapsed;
    void ISessionView.SetStatus(string text) => StatusLeft.Text = text;
    void ISessionView.SetStatistics(string text) => StatusRight.Text = text;
    void ISessionView.SetTitle(string text) => TitleText.Text = text;

    void ISessionView.AttachSession(VncClient client)
    {
        View.Attach(client);
        View.Focus();
        Keyboard.Focus(View);
    }

    void ISessionView.AttachAgent(IAgentInput input) => View.AttachAgent(input);
    void ISessionView.SetAgentPictureSize(int width, int height) => View.SetAgentPictureSize(width, height);

    void ISessionView.FocusPicture()
    {
        View.Focus();
        Keyboard.Focus(View);
    }

    void ISessionView.DetachSession() => View.Detach();
    void ISessionView.FitToRemote(int width, int height) => FitWindowToRemote(width, height);
    void ISessionView.SetScrollInjector(IScrollInjector? injector) => View.ScrollInjector = injector;
    void ISessionView.UseMacKeyboard() => View.Translator.MacMode = true;
    void ISessionView.ReleaseKeys() => View.ReleaseAllKeys();
    void ISessionView.SetAudioAvailable(bool available) => AudioToggle.IsEnabled = available;
    void ISessionView.Beep() => System.Media.SystemSounds.Beep.Play();

    VncCredentials? ISessionView.PromptCredentials(CredentialKind kind, string prompt, string? username, out bool remember)
    {
        var dlg = new CredentialsDialog(kind, prompt, username) { Owner = this };
        remember = false;
        if (dlg.ShowDialog() != true) return null;
        remember = dlg.Remember;
        return new VncCredentials(dlg.Username, dlg.Password);
    }

    void ISessionView.ShowMessage(string title, string text, bool warning) =>
        MessageBox.Show(this, text, title, MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    // ------------------------------------------------------------------ picture mode

    /// <summary>Enables each picture entry the current server and agent can deliver, with the reason on the disabled ones.</summary>
    public void RefreshPictureMenu()
    {
        foreach (ComboBoxItem item in PictureBox.Items)
        {
            var info = (PictureModes.Info)item.Tag;
            string? why = _controller.WhyUnavailable(info);
            item.IsEnabled = why == null;
            item.ToolTip = why == null ? info.Description : $"{info.Description}\n\nNot available: {why}";
        }
        PictureDefaultItem.IsEnabled = _controller.CanMakePictureDefault;
    }

    private void OnPictureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_pictureUpdating || PictureBox.SelectedItem is not ComboBoxItem { Tag: PictureModes.Info info }) return;
        _controller.SelectPicture(info);
        View.Focus();
    }

    private void OnPictureMakeDefault(object sender, RoutedEventArgs e) => _controller.MakePictureDefault();

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

    private void OnReconnect(object sender, RoutedEventArgs e) => _ = _controller.ConnectAsync();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        _controller.Disconnect();
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

    // ------------------------------------------------------------------ clipboard and files

    private void OnClipboardToggled(object sender, RoutedEventArgs e) => _controller.SyncClipboard = ClipboardToggle.IsChecked == true;

    private async void OnPasteToRemote(object sender, RoutedEventArgs e) => await _controller.PasteToRemoteAsync();

    private async void OnSendFiles(object sender, RoutedEventArgs e)
    {
        if (!_controller.CanSendFiles)
        {
            MessageBox.Show(this, SessionController.NoFileChannelMessage, "Send files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Send files to remote" };
        if (dlg.ShowDialog(this) != true) return;
        await _controller.UploadAsync(dlg.FileNames);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await _controller.UploadAsync(files);
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

    private void OnAudioToggled(object sender, RoutedEventArgs e) => _controller.SetAudio(AudioToggle.IsChecked == true);

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
}
