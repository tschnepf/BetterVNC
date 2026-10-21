using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterVnc.Viewer.Core.Models;

namespace BetterVnc.Viewer.Views;

/// <summary>Creates or edits a connection profile; closes with true when the person saved.</summary>
public partial class ConnectionEditorWindow : Window
{
    public ConnectionProfile Profile { get; } = new();
    private const string KeepPassword = "\u0001keep\u0001";
    private bool _ready;
    private bool _pairKeyKept;

    public ConnectionEditorWindow()
    {
        InitializeComponent();
    }

    public ConnectionEditorWindow(ConnectionProfile profile) : this()
    {
        Profile = profile;
        Title = string.IsNullOrEmpty(profile.Name) ? "New connection" : $"Edit {profile.Name}";

        ServerBox.ItemsSource = App.Profiles.UnraidServers;

        NameBox.Text = profile.Name;
        KindBox.SelectedIndex = (int)profile.Kind;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.Username ?? "";
        if (profile.ProtectedPassword != null) PasswordBox.Text = KeepPassword;
        ServerBox.SelectedItem = App.Profiles.FindServer(profile.UnraidServerId);
        VmBox.Text = profile.UnraidVmName ?? "";
        MacStandard.IsChecked = profile.MacModifiers == MacModifierMode.Standard;
        MacCtrlCmd.IsChecked = profile.MacModifiers == MacModifierMode.CtrlAsCommand;
        MacSwap.IsChecked = profile.MacModifiers == MacModifierMode.SwapWinAlt;
        FitBox.IsChecked = profile.ScaleMode == ScaleMode.Fit;
        FullscreenBox.IsChecked = profile.StartFullscreen;
        GrabBox.IsChecked = profile.CaptureWindowsKeys;
        CursorBox.IsChecked = profile.LocalCursor;
        ClipboardBox.IsChecked = profile.SyncClipboard;
        AudioBox.IsChecked = profile.EnableAudio;
        foreach (var info in PictureModes.All)
            QualityBox.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info, [ToolTip.TipProperty] = info.Description });
        QualityBox.SelectedIndex = Math.Max(0, Array.IndexOf(PictureModes.All, PictureModes.Get(profile.PictureMode)));
        ScrollSlider.Value = Math.Clamp(profile.ScrollClicksPerNotch, 1, 20);
        TransferBox.SelectedIndex = (int)profile.FileTransfer;
        SftpHostBox.Text = profile.SftpHost ?? "";
        SftpPortBox.Text = profile.SftpPort.ToString();
        SftpUserBox.Text = profile.SftpUsername ?? "";
        if (profile.SftpProtectedPassword != null) SftpPasswordBox.Text = KeepPassword;
        SftpKeyBox.Text = profile.SftpPrivateKeyPath ?? "";
        SftpFolderBox.Text = profile.RemoteDropFolder ?? "";
        SmbPathBox.Text = profile.SmbPath ?? "";
        SmbUserBox.Text = profile.SmbUsername ?? "";
        if (profile.SmbProtectedPassword != null) SmbPasswordBox.Text = KeepPassword;
        AgentBox.IsChecked = profile.AgentEnabled || profile.AgentOnly;
        AgentOnlyBox.IsChecked = profile.AgentOnly;
        AgentHostBox.Text = profile.AgentHost ?? "";
        AgentPortBox.Text = profile.AgentPort.ToString();
        AgentPairRadio.IsChecked = profile.AgentAuth == AgentAuthMode.Pair;
        AgentTokenRadio.IsChecked = profile.AgentAuth == AgentAuthMode.Token;
        _pairKeyKept = profile.AgentProtectedPairKey != null;
        if (profile.AgentProtectedToken != null) AgentTokenBox.Text = KeepPassword;

        _ready = true;
        UpdateKind(userChange: false);
        OnTransferChanged(this, null);
        OnAgentChanged(this, null);
    }

    private void OnAgentChanged(object? sender, RoutedEventArgs? e)
    {
        if (!_ready) return;
        // Without VNC the agent is the connection: it cannot be switched off, and the VNC port means nothing.
        bool agentOnly = AgentOnlyBox.IsChecked == true;
        if (agentOnly) AgentBox.IsChecked = true;
        AgentBox.IsEnabled = !agentOnly;
        PortBox.IsEnabled = !agentOnly;
        AgentPanel.IsVisible = AgentBox.IsChecked == true;
        OnAgentAuthChanged(this, null);
    }

    private void OnAgentAuthChanged(object? sender, RoutedEventArgs? e)
    {
        if (!_ready) return;
        bool token = AgentTokenRadio.IsChecked == true;
        AgentTokenPanel.IsVisible = token;
        AgentPairedPanel.IsVisible = !token && _pairKeyKept;
    }

    private void OnForgetPairing(object? sender, RoutedEventArgs e)
    {
        _pairKeyKept = false;
        OnAgentAuthChanged(this, null);
    }

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e) => UpdateKind(userChange: true);

    private void UpdateKind(bool userChange)
    {
        if (!_ready) return;
        var kind = (ConnectionKind)Math.Max(0, KindBox.SelectedIndex);
        UnraidPanel.IsVisible = kind == ConnectionKind.UnraidVm;
        MacPanel.IsVisible = kind == ConnectionKind.Mac;
        UserLabel.IsVisible = UserBox.IsVisible = kind == ConnectionKind.Mac;
        PasswordHint.Text = kind switch
        {
            ConnectionKind.Mac => "Your macOS account password (Apple Remote Desktop authentication). If the Mac only allows \"VNC viewers with password\", leave the user name empty and enter that VNC password.",
            ConnectionKind.UnraidVm => "The VNC password from the VM settings, if any. When the VM is linked to an Unraid server the password is read from the VM definition automatically.",
            _ => "Optional VNC password. You are prompted if the server asks for one.",
        };
        if (kind == ConnectionKind.UnraidVm && ServerBox.SelectedItem == null && App.Profiles.UnraidServers.Count > 0)
            ServerBox.SelectedIndex = 0;
        // When a person switches a connection to macOS, default to the faster scroll that Macs need.
        if (userChange && kind == ConnectionKind.Mac && ScrollSlider.Value < 6) ScrollSlider.Value = 6;
    }

    private void OnTransferChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (!_ready) return;
        SftpPanel.IsVisible = TransferBox.SelectedIndex == 1;
        SmbPanel.IsVisible = TransferBox.SelectedIndex == 2;
    }

    private async void OnBrowseVms(object? sender, RoutedEventArgs e)
    {
        if (ServerBox.SelectedItem is not UnraidServer server)
        {
            await MessageDialog.ShowAsync(this, "Add an Unraid server first (main window, \"Add Unraid server\").", "Browse VMs", MessageButtons.Ok, MessageIcon.Info);
            return;
        }
        var dlg = new UnraidServerWindow(server, pickOnly: true);
        if (await dlg.ShowDialog<bool>(this) && dlg.PickedVm != null)
        {
            VmBox.Text = dlg.PickedVm.Name;
            HostBox.Text = server.Host;
            if (dlg.PickedVm.VncPort is { } p) PortBox.Text = p.ToString();
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = dlg.PickedVm.Name;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        string hostText = HostBox.Text ?? "", vmText = VmBox.Text ?? "", nameText = NameBox.Text ?? "";
        bool agentHostOnly = AgentOnlyBox.IsChecked == true && !string.IsNullOrWhiteSpace(AgentHostBox.Text);
        if (string.IsNullOrWhiteSpace(hostText) && KindBox.SelectedIndex != (int)ConnectionKind.UnraidVm && !agentHostOnly)
        {
            await MessageDialog.ShowAsync(this, "Enter a host name or IP address.", "Connection", MessageButtons.Ok, MessageIcon.Warning);
            return;
        }
        var p = Profile;
        // Checked before anything is written, so a refused save leaves the profile as it was.
        bool agentOn = AgentBox.IsChecked == true || AgentOnlyBox.IsChecked == true;
        bool tokenMode = AgentTokenRadio.IsChecked == true;
        string token = AgentTokenBox.Text ?? "";
        bool hasToken = token == KeepPassword ? !string.IsNullOrEmpty(p.AgentProtectedToken) : token.Trim().Length > 0;
        if (agentOn && tokenMode && !hasToken)
        {
            await MessageDialog.ShowAsync(this, "Enter the token or password from the agent's settings (tray or menu bar icon on the remote, or 'bettervnc-agent token show'), or choose pairing.", "Guest agent", MessageButtons.Ok, MessageIcon.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535) port = 5900;
        var (cleanHost, hostPort) = HostParser.Parse(hostText);
        if (hostPort is { } hp && (PortBox.Text ?? "").Trim() is "" or "5900") port = hp;
        p.Kind = (ConnectionKind)Math.Max(0, KindBox.SelectedIndex);
        p.Name = string.IsNullOrWhiteSpace(nameText) ? (string.IsNullOrWhiteSpace(vmText) ? hostText : vmText) : nameText.Trim();
        p.Host = cleanHost;
        p.Port = port;
        p.Username = p.Kind == ConnectionKind.Mac && !string.IsNullOrWhiteSpace(UserBox.Text) ? UserBox.Text.Trim() : null;
        if (PasswordBox.Text != KeepPassword) p.ProtectedPassword = App.Profiles.Protect(PasswordBox.Text ?? "");
        p.UnraidServerId = p.Kind == ConnectionKind.UnraidVm ? (ServerBox.SelectedItem as UnraidServer)?.Id : null;
        p.UnraidVmName = p.Kind == ConnectionKind.UnraidVm ? vmText.Trim() : null;
        if (p.Kind == ConnectionKind.UnraidVm && string.IsNullOrEmpty(p.Host) && ServerBox.SelectedItem is UnraidServer s) p.Host = s.Host;
        p.MacModifiers = MacCtrlCmd.IsChecked == true ? MacModifierMode.CtrlAsCommand : MacSwap.IsChecked == true ? MacModifierMode.SwapWinAlt : MacModifierMode.Standard;
        p.ScaleMode = FitBox.IsChecked == true ? ScaleMode.Fit : ScaleMode.OneToOne;
        p.StartFullscreen = FullscreenBox.IsChecked == true;
        p.CaptureWindowsKeys = GrabBox.IsChecked == true;
        p.LocalCursor = CursorBox.IsChecked == true;
        p.SyncClipboard = ClipboardBox.IsChecked == true;
        p.EnableAudio = AudioBox.IsChecked == true;
        var picture = (QualityBox.SelectedItem as ComboBoxItem)?.Tag as PictureModes.Info ?? PictureModes.All[0];
        p.PictureMode = picture.Mode;
        // The VNC side of the choice, also readable by older builds. A stream mode rides on lossless VNC.
        var vnc = picture.IsStream ? PictureModes.StreamFallback : picture;
        (p.JpegQuality, p.CompressionLevel) = (vnc.JpegQuality, vnc.CompressionLevel);
        p.ScrollClicksPerNotch = (int)Math.Round(ScrollSlider.Value);
        p.FileTransfer = (FileTransferMode)Math.Max(0, TransferBox.SelectedIndex);
        p.SftpHost = Trimmed(SftpHostBox.Text);
        p.SftpPort = int.TryParse(SftpPortBox.Text, out var sp) && sp > 0 ? sp : 22;
        p.SftpUsername = Trimmed(SftpUserBox.Text);
        if (SftpPasswordBox.Text != KeepPassword) p.SftpProtectedPassword = App.Profiles.Protect(SftpPasswordBox.Text ?? "");
        p.SftpPrivateKeyPath = Trimmed(SftpKeyBox.Text);
        p.RemoteDropFolder = Trimmed(SftpFolderBox.Text);
        p.SmbPath = Trimmed(SmbPathBox.Text);
        p.SmbUsername = Trimmed(SmbUserBox.Text);
        if (SmbPasswordBox.Text != KeepPassword) p.SmbProtectedPassword = App.Profiles.Protect(SmbPasswordBox.Text ?? "");
        p.AgentOnly = AgentOnlyBox.IsChecked == true;
        p.AgentEnabled = AgentBox.IsChecked == true || p.AgentOnly;
        // A stream mode is the only picture there is without VNC.
        if (p.AgentOnly && !picture.IsStream) p.PictureMode = PictureMode.StreamQuality;
        p.AgentHost = Trimmed(AgentHostBox.Text);
        p.AgentPort = int.TryParse(AgentPortBox.Text, out var ap) && ap > 0 && ap <= 65535 ? ap : 5970;
        p.AgentAuth = tokenMode ? AgentAuthMode.Token : AgentAuthMode.Pair;
        if (token != KeepPassword) p.AgentProtectedToken = App.Profiles.Protect(token.Trim());
        if (!_pairKeyKept) p.AgentProtectedPairKey = null;
        p.NotifyChanged();
        Close(true);
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
