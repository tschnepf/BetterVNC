using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BetterVnc.App.Services;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;

namespace BetterVnc.App.Views;

public partial class MainWindow : Window
{
    private readonly ICollectionView _connectionsView;

    public MainWindow()
    {
        InitializeComponent();
        _connectionsView = CollectionViewSource.GetDefaultView(App.Profiles.Connections);
        _connectionsView.SortDescriptions.Add(new SortDescription(nameof(ConnectionProfile.LastConnected), ListSortDirection.Descending));
        _connectionsView.SortDescriptions.Add(new SortDescription(nameof(ConnectionProfile.Name), ListSortDirection.Ascending));
        _connectionsView.Filter = o => o is ConnectionProfile p && (string.IsNullOrWhiteSpace(SearchBox.Text)
            || p.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)
            || p.Host.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)
            || (p.UnraidVmName?.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) ?? false));
        ConnectionList.ItemsSource = _connectionsView;
        ServerList.ItemsSource = App.Profiles.UnraidServers;
        App.Profiles.Connections.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
        SessionManager.Changed += UpdateSessionControls;
        UpdateSessionControls();
    }

    private void UpdateEmptyHint() => EmptyHint.Visibility = App.Profiles.Connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateSessionControls()
    {
        int n = SessionManager.Count;
        DisconnectAllButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisconnectAllButton.Content = n > 1 ? $"Disconnect all ({n})" : "Disconnect all";
    }

    private void OnDisconnectAll(object sender, RoutedEventArgs e) => SessionManager.CloseAll();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        int n = SessionManager.Count;
        if (n == 0) return;
        var answer = MessageBox.Show(this, n == 1 ? "Close 1 open session?" : $"Close {n} open sessions?", "BetterVNC",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        SessionManager.CloseAll();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _connectionsView.Refresh();

    private ConnectionProfile? Selected => ConnectionList.SelectedItem as ConnectionProfile;

    // ------------------------------------------------------------------ connections

    private void OnNewConnection(object sender, RoutedEventArgs e)
    {
        var profile = new ConnectionProfile();
        var dlg = new ConnectionEditorWindow(profile) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        App.Profiles.Connections.Add(profile);
        App.Profiles.Save();
    }

    private void OnConnectionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) _ = ConnectAsync(Selected, fullscreen: null);
    }

    private void OnConnectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected != null) { _ = ConnectAsync(Selected, null); e.Handled = true; }
        if (e.Key == Key.Delete && Selected != null) { OnDeleteMenu(sender, e); e.Handled = true; }
    }

    private void OnConnectMenu(object sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, null); }
    private void OnConnectFullscreenMenu(object sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, true); }
    private void OnOpenAnotherSessionMenu(object sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, null, forceNewWindow: true); }

    private void OnEditMenu(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dlg = new ConnectionEditorWindow(Selected) { Owner = this };
        if (dlg.ShowDialog() == true) { App.Profiles.Save(); _connectionsView.Refresh(); }
    }

    private void OnDuplicateMenu(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(Selected);
        var copy = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(json)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Selected.Name + " (copy)";
        copy.LastConnected = null;
        App.Profiles.Connections.Add(copy);
        App.Profiles.Save();
    }

    private void OnDeleteMenu(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        if (MessageBox.Show(this, $"Delete \"{Selected.Name}\"?", "BetterVNC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Profiles.Connections.Remove(Selected);
        App.Profiles.Save();
    }

    /// <param name="fullscreen">True to start this session fullscreen regardless of the profile setting; null to follow the profile.</param>
    /// <param name="forceNewWindow">Open a second window even when a session for this profile is already open.</param>
    private async Task ConnectAsync(ConnectionProfile profile, bool? fullscreen, bool forceNewWindow = false)
    {
        if (!forceNewWindow && SessionManager.Find(profile) is { } existing)
        {
            existing.BringToFront();
            return;
        }

        string? host = null;
        int? port = null;
        string? password = null;
        string? guestAddress = null;

        if (profile.Kind == ConnectionKind.UnraidVm && App.Profiles.FindServer(profile.UnraidServerId) is { } server && !string.IsNullOrEmpty(profile.UnraidVmName))
        {
            // Resolve the current VNC port (and password) from the Unraid host; ports change on every VM start.
            try
            {
                Cursor = Cursors.Wait;
                var svc = new UnraidService(server, App.Profiles.Secrets);
                var vm = await svc.GetVmAsync(profile.UnraidVmName);
                if (!vm.IsRunning)
                {
                    Cursor = null;
                    var answer = MessageBox.Show(this, $"VM \"{vm.Name}\" is {vm.State}. Start it now?", "BetterVNC", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes) return;
                    Cursor = Cursors.Wait;
                    await svc.StartVmAsync(vm.Name);
                    for (int i = 0; i < 20 && vm.VncPort == null; i++)
                    {
                        await Task.Delay(1000);
                        vm = await svc.GetVmAsync(profile.UnraidVmName);
                    }
                }
                host = server.Host;
                port = vm.VncPort;
                password = vm.VncPassword;
                guestAddress = vm.GuestAddress;
                if (port == null)
                {
                    MessageBox.Show(this, $"VM \"{vm.Name}\" has no VNC display (is it using a passthrough GPU?).", "BetterVNC", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not query the Unraid server over SSH:\n{ex.Message}\n\nConnecting with the saved host and port instead.", "BetterVNC", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Cursor = null;
            }
        }

        var win = new SessionWindow(profile, host, port, password, fullscreen: fullscreen == true, guestAddress: guestAddress);
        win.Show();
    }

    // ------------------------------------------------------------------ Unraid servers

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var server = new UnraidServer();
        var dlg = new UnraidServerWindow(server) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        App.Profiles.UnraidServers.Add(server);
        App.Profiles.Save();
    }

    private UnraidServer? SelectedServer => ServerList.SelectedItem as UnraidServer;

    private void OnServerDoubleClick(object sender, MouseButtonEventArgs e) => BrowseServer(SelectedServer);
    private void OnBrowseServerMenu(object sender, RoutedEventArgs e) => BrowseServer(SelectedServer);
    private void OnBrowseServer(object sender, RoutedEventArgs e) => BrowseServer((sender as FrameworkElement)?.DataContext as UnraidServer);

    private void BrowseServer(UnraidServer? server)
    {
        if (server == null) return;
        var dlg = new UnraidServerWindow(server) { Owner = this };
        dlg.ShowDialog();
        App.Profiles.Save();
    }

    private void OnEditServerMenu(object sender, RoutedEventArgs e)
    {
        if (SelectedServer == null) return;
        var dlg = new UnraidServerWindow(SelectedServer) { Owner = this };
        if (dlg.ShowDialog() == true) App.Profiles.Save();
    }

    private void OnDeleteServerMenu(object sender, RoutedEventArgs e)
    {
        if (SelectedServer == null) return;
        if (MessageBox.Show(this, $"Delete Unraid server \"{SelectedServer.Name}\"?", "BetterVNC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Profiles.UnraidServers.Remove(SelectedServer);
        App.Profiles.Save();
    }
}
