using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Services;

namespace BetterVnc.Viewer.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ServerList.ItemsSource = App.Profiles.UnraidServers;
        SearchBox.TextChanged += (_, _) => RefreshConnections();
        App.Profiles.Connections.CollectionChanged += OnConnectionsChanged;
        RefreshConnections();
        SessionManager.Changed += UpdateSessionControls;
        UpdateSessionControls();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            SessionManager.Changed -= UpdateSessionControls;
            App.Profiles.Connections.CollectionChanged -= OnConnectionsChanged;
        };
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshConnections();

    /// <summary>
    /// Shows the connections that match the filter, most recently used first, then by name; keeps the selection. Called
    /// when the list or the filter changes, or after an edit (not on every connect, so the list does not jump under the pointer).
    /// </summary>
    private void RefreshConnections()
    {
        var selected = Selected;
        string filter = SearchBox.Text ?? "";
        ConnectionList.ItemsSource = App.Profiles.Connections
            .Where(p => string.IsNullOrWhiteSpace(filter)
                        || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || p.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (p.UnraidVmName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(p => p.LastConnected ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
        if (selected != null) ConnectionList.SelectedItem = selected;
        EmptyHint.IsVisible = App.Profiles.Connections.Count == 0;
    }

    private void UpdateSessionControls()
    {
        int n = SessionManager.Count;
        DisconnectAllButton.IsVisible = n > 0;
        DisconnectAllButton.Content = n > 1 ? $"Disconnect all ({n})" : "Disconnect all";
    }

    private void OnDisconnectAll(object? sender, RoutedEventArgs e) => SessionManager.CloseAll();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        int n = SessionManager.Count;
        if (n == 0 || _closeConfirmed) return;
        // The answer comes back later: keep the window, then close it again once the person agreed.
        e.Cancel = true;
        var answer = await MessageDialog.ShowAsync(this, n == 1 ? "Close 1 open session?" : $"Close {n} open sessions?", "BetterVNC",
            MessageButtons.YesNo, MessageIcon.Question, MessageResult.No);
        if (answer != MessageResult.Yes) return;
        SessionManager.CloseAll();
        _closeConfirmed = true;
        Close();
    }

    private ConnectionProfile? Selected => ConnectionList.SelectedItem as ConnectionProfile;

    private static bool FromItem(RoutedEventArgs e) =>
        (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null;

    // ------------------------------------------------------------------ connections

    private async void OnNewConnection(object? sender, RoutedEventArgs e)
    {
        var profile = new ConnectionProfile();
        if (await new ConnectionEditorWindow(profile).ShowDialog<bool>(this) != true) return;
        App.Profiles.Connections.Add(profile);
        App.Profiles.Save();
    }

    private void OnConnectionDoubleClick(object? sender, TappedEventArgs e)
    {
        if (FromItem(e) && Selected != null) _ = ConnectAsync(Selected, fullscreen: null);
    }

    private void OnConnectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected != null) { _ = ConnectAsync(Selected, null); e.Handled = true; }
        if (e.Key == Key.Delete && Selected != null) { OnDeleteMenu(sender, e); e.Handled = true; }
    }

    private void OnConnectMenu(object? sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, null); }
    private void OnConnectFullscreenMenu(object? sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, true); }
    private void OnOpenAnotherSessionMenu(object? sender, RoutedEventArgs e) { if (Selected != null) _ = ConnectAsync(Selected, null, forceNewWindow: true); }

    private async void OnEditMenu(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        if (await new ConnectionEditorWindow(profile).ShowDialog<bool>(this) == true) { App.Profiles.Save(); RefreshConnections(); }
    }

    private void OnDuplicateMenu(object? sender, RoutedEventArgs e)
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

    private async void OnDeleteMenu(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        if (await MessageDialog.ShowAsync(this, $"Delete \"{profile.Name}\"?", "BetterVNC", MessageButtons.YesNo, MessageIcon.Question) != MessageResult.Yes) return;
        App.Profiles.Connections.Remove(profile);
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
                Cursor = new Cursor(StandardCursorType.Wait);
                var svc = new UnraidService(server, App.Profiles.Secrets);
                var vm = await svc.GetVmAsync(profile.UnraidVmName);
                if (!vm.IsRunning)
                {
                    Cursor = null;
                    var answer = await MessageDialog.ShowAsync(this, $"VM \"{vm.Name}\" is {vm.State}. Start it now?", "BetterVNC", MessageButtons.YesNo, MessageIcon.Question);
                    if (answer != MessageResult.Yes) return;
                    Cursor = new Cursor(StandardCursorType.Wait);
                    await svc.StartVmAsync(vm.Name);
                    for (int i = 0; i < 20 && vm.VncPort == null && !profile.AgentOnly; i++)
                    {
                        await Task.Delay(1000);
                        vm = await svc.GetVmAsync(profile.UnraidVmName);
                    }
                }
                host = server.Host;
                port = vm.VncPort;
                password = vm.VncPassword;
                guestAddress = vm.GuestAddress;
                if (port == null && !profile.AgentOnly) // without VNC (a passthrough GPU) the agent is all it takes
                {
                    Cursor = null;
                    await MessageDialog.ShowAsync(this, $"VM \"{vm.Name}\" has no VNC display (is it using a passthrough GPU?).", "BetterVNC", MessageButtons.Ok, MessageIcon.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                Cursor = null;
                await MessageDialog.ShowAsync(this, $"Could not query the Unraid server over SSH:\n{ex.Message}\n\nConnecting with the saved host and port instead.", "BetterVNC", MessageButtons.Ok, MessageIcon.Warning);
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

    private async void OnAddServer(object? sender, RoutedEventArgs e)
    {
        var server = new UnraidServer();
        if (await new UnraidServerWindow(server).ShowDialog<bool>(this) != true) return;
        App.Profiles.UnraidServers.Add(server);
        App.Profiles.Save();
    }

    private UnraidServer? SelectedServer => ServerList.SelectedItem as UnraidServer;

    private void OnServerDoubleClick(object? sender, TappedEventArgs e)
    {
        if (FromItem(e)) BrowseServer(SelectedServer);
    }

    private void OnBrowseServerMenu(object? sender, RoutedEventArgs e) => BrowseServer(SelectedServer);
    private void OnBrowseServer(object? sender, RoutedEventArgs e) => BrowseServer((sender as Control)?.DataContext as UnraidServer);

    private async void BrowseServer(UnraidServer? server)
    {
        if (server == null) return;
        await new UnraidServerWindow(server).ShowDialog<bool>(this);
        App.Profiles.Save();
    }

    private async void OnEditServerMenu(object? sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        if (await new UnraidServerWindow(server).ShowDialog<bool>(this) == true) App.Profiles.Save();
    }

    private async void OnDeleteServerMenu(object? sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server) return;
        if (await MessageDialog.ShowAsync(this, $"Delete Unraid server \"{server.Name}\"?", "BetterVNC", MessageButtons.YesNo, MessageIcon.Question) != MessageResult.Yes) return;
        App.Profiles.UnraidServers.Remove(server);
        App.Profiles.Save();
    }
}
