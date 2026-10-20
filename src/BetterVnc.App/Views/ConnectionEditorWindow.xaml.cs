using System.Windows;
using System.Windows.Controls;
using BetterVnc.Viewer.Core.Models;

namespace BetterVnc.App.Views;

public partial class ConnectionEditorWindow : Window
{
    public ConnectionProfile Profile { get; }
    private const string KeepPassword = "\u0001keep\u0001";

    public ConnectionEditorWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        Profile = profile;
        Title = string.IsNullOrEmpty(profile.Name) ? "New connection" : $"Edit {profile.Name}";

        ServerBox.ItemsSource = App.Profiles.UnraidServers;

        NameBox.Text = profile.Name;
        KindBox.SelectedIndex = (int)profile.Kind;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.Username ?? "";
        if (profile.ProtectedPassword != null) PasswordBox.Password = KeepPassword;
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
            QualityBox.Items.Add(new ComboBoxItem { Content = info.Label, Tag = info, ToolTip = info.Description });
        QualityBox.SelectedIndex = Math.Max(0, Array.IndexOf(PictureModes.All, PictureModes.Get(profile.PictureMode)));
        ScrollSlider.Value = Math.Clamp(profile.ScrollClicksPerNotch, 1, 20);
        TransferBox.SelectedIndex = (int)profile.FileTransfer;
        SftpHostBox.Text = profile.SftpHost ?? "";
        SftpPortBox.Text = profile.SftpPort.ToString();
        SftpUserBox.Text = profile.SftpUsername ?? "";
        if (profile.SftpProtectedPassword != null) SftpPasswordBox.Password = KeepPassword;
        SftpKeyBox.Text = profile.SftpPrivateKeyPath ?? "";
        SftpFolderBox.Text = profile.RemoteDropFolder ?? "";
        SmbPathBox.Text = profile.SmbPath ?? "";
        SmbUserBox.Text = profile.SmbUsername ?? "";
        if (profile.SmbProtectedPassword != null) SmbPasswordBox.Password = KeepPassword;
        AgentBox.IsChecked = profile.AgentEnabled;
        AgentHostBox.Text = profile.AgentHost ?? "";
        AgentPortBox.Text = profile.AgentPort.ToString();
        AgentPairRadio.IsChecked = profile.AgentAuth == AgentAuthMode.Pair;
        AgentTokenRadio.IsChecked = profile.AgentAuth == AgentAuthMode.Token;
        _pairKeyKept = profile.AgentProtectedPairKey != null;
        if (profile.AgentProtectedToken != null) AgentTokenBox.Password = KeepPassword;

        OnKindChanged(this, null!);
        OnTransferChanged(this, null!);
        OnAgentChanged(this, null!);
    }

    private bool _pairKeyKept;

    private void OnAgentChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || AgentPanel == null) return;
        AgentPanel.Visibility = AgentBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OnAgentAuthChanged(this, null!);
    }

    private void OnAgentAuthChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || AgentTokenPanel == null) return;
        bool token = AgentTokenRadio.IsChecked == true;
        AgentTokenPanel.Visibility = token ? Visibility.Visible : Visibility.Collapsed;
        AgentPairedPanel.Visibility = !token && _pairKeyKept ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnForgetPairing(object sender, RoutedEventArgs e)
    {
        _pairKeyKept = false;
        OnAgentAuthChanged(this, null!);
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || UnraidPanel == null) return;
        var kind = (ConnectionKind)Math.Max(0, KindBox.SelectedIndex);
        UnraidPanel.Visibility = kind == ConnectionKind.UnraidVm ? Visibility.Visible : Visibility.Collapsed;
        MacPanel.Visibility = kind == ConnectionKind.Mac ? Visibility.Visible : Visibility.Collapsed;
        UserLabel.Visibility = UserBox.Visibility = kind == ConnectionKind.Mac ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Text = kind switch
        {
            ConnectionKind.Mac => "Your macOS account password (Apple Remote Desktop authentication). If the Mac only allows \"VNC viewers with password\", leave the user name empty and enter that VNC password.",
            ConnectionKind.UnraidVm => "The VNC password from the VM settings, if any. When the VM is linked to an Unraid server the password is read from the VM definition automatically.",
            _ => "Optional VNC password. You are prompted if the server asks for one.",
        };
        if (kind == ConnectionKind.UnraidVm && ServerBox.SelectedItem == null && App.Profiles.UnraidServers.Count > 0)
            ServerBox.SelectedIndex = 0;
        // When a person switches a connection to macOS, default to the faster scroll that Macs need.
        if (e != null && kind == ConnectionKind.Mac && ScrollSlider != null && ScrollSlider.Value < 6) ScrollSlider.Value = 6;
    }

    private void OnTransferChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || SftpPanel == null) return;
        SftpPanel.Visibility = TransferBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SmbPanel.Visibility = TransferBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBrowseVms(object sender, RoutedEventArgs e)
    {
        if (ServerBox.SelectedItem is not UnraidServer server)
        {
            MessageBox.Show(this, "Add an Unraid server first (main window, \"Add Unraid server\").", "Browse VMs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new UnraidServerWindow(server, pickOnly: true) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.PickedVm != null)
        {
            VmBox.Text = dlg.PickedVm.Name;
            HostBox.Text = server.Host;
            if (dlg.PickedVm.VncPort is { } p) PortBox.Text = p.ToString();
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = dlg.PickedVm.Name;
        }
        await Task.CompletedTask;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text) && KindBox.SelectedIndex != (int)ConnectionKind.UnraidVm)
        {
            MessageBox.Show(this, "Enter a host name or IP address.", "Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port <= 0 || port > 65535) port = 5900;
        var (cleanHost, hostPort) = HostParser.Parse(HostBox.Text);
        if (hostPort is { } hp && PortBox.Text.Trim() is "" or "5900") port = hp;
        var p = Profile;
        p.Kind = (ConnectionKind)Math.Max(0, KindBox.SelectedIndex);
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? (string.IsNullOrWhiteSpace(VmBox.Text) ? HostBox.Text : VmBox.Text) : NameBox.Text.Trim();
        p.Host = cleanHost;
        p.Port = port;
        p.Username = p.Kind == ConnectionKind.Mac && !string.IsNullOrWhiteSpace(UserBox.Text) ? UserBox.Text.Trim() : null;
        if (PasswordBox.Password != KeepPassword) p.ProtectedPassword = App.Profiles.Protect(PasswordBox.Password);
        p.UnraidServerId = p.Kind == ConnectionKind.UnraidVm ? (ServerBox.SelectedItem as UnraidServer)?.Id : null;
        p.UnraidVmName = p.Kind == ConnectionKind.UnraidVm ? VmBox.Text.Trim() : null;
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
        p.SftpHost = string.IsNullOrWhiteSpace(SftpHostBox.Text) ? null : SftpHostBox.Text.Trim();
        p.SftpPort = int.TryParse(SftpPortBox.Text, out var sp) && sp > 0 ? sp : 22;
        p.SftpUsername = string.IsNullOrWhiteSpace(SftpUserBox.Text) ? null : SftpUserBox.Text.Trim();
        if (SftpPasswordBox.Password != KeepPassword) p.SftpProtectedPassword = App.Profiles.Protect(SftpPasswordBox.Password);
        p.SftpPrivateKeyPath = string.IsNullOrWhiteSpace(SftpKeyBox.Text) ? null : SftpKeyBox.Text.Trim();
        p.RemoteDropFolder = string.IsNullOrWhiteSpace(SftpFolderBox.Text) ? null : SftpFolderBox.Text.Trim();
        p.SmbPath = string.IsNullOrWhiteSpace(SmbPathBox.Text) ? null : SmbPathBox.Text.Trim();
        p.SmbUsername = string.IsNullOrWhiteSpace(SmbUserBox.Text) ? null : SmbUserBox.Text.Trim();
        if (SmbPasswordBox.Password != KeepPassword) p.SmbProtectedPassword = App.Profiles.Protect(SmbPasswordBox.Password);
        p.AgentEnabled = AgentBox.IsChecked == true;
        p.AgentHost = string.IsNullOrWhiteSpace(AgentHostBox.Text) ? null : AgentHostBox.Text.Trim();
        p.AgentPort = int.TryParse(AgentPortBox.Text, out var ap) && ap > 0 && ap <= 65535 ? ap : 5970;
        p.AgentAuth = AgentTokenRadio.IsChecked == true ? AgentAuthMode.Token : AgentAuthMode.Pair;
        if (AgentTokenBox.Password != KeepPassword) p.AgentProtectedToken = App.Profiles.Protect(AgentTokenBox.Password.Trim());
        if (!_pairKeyKept) p.AgentProtectedPairKey = null;
        if (p.AgentEnabled && p.AgentAuth == AgentAuthMode.Token && string.IsNullOrEmpty(p.AgentProtectedToken))
        {
            MessageBox.Show(this, "Enter the token or password from the agent's settings (tray or menu bar icon on the remote, or 'bettervnc-agent token show'), or choose pairing.", "Guest agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        p.NotifyChanged();
        DialogResult = true;
    }
}
