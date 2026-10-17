using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterVnc.App.Models;
using BetterVnc.App.Services;

namespace BetterVnc.App.Views;

public partial class UnraidServerWindow : Window
{
    public UnraidServer Server { get; }
    public UnraidVmInfo? PickedVm { get; private set; }
    private readonly bool _pickOnly;
    private const string KeepPassword = "\u0001keep\u0001";

    public sealed class VmRow
    {
        public required UnraidVmInfo Info { get; init; }
        public string Name => Info.Name;
        public string Detail => Info.IsRunning ? $"{Info.State} · VNC port {Info.VncPort?.ToString() ?? "?"}{(Info.VncPassword != null ? " · password set" : "")}" : Info.State;
        public string Warnings => Info.Warnings;
        public Visibility WarningVisibility => Info.Warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StartVisibility => Info.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ConnectVisibility => Info.IsRunning && Info.VncPort != null ? Visibility.Visible : Visibility.Collapsed;
        public Brush StateBrush => Info.IsRunning ? (Brush)Application.Current.Resources["SuccessBrush"] : (Brush)Application.Current.Resources["MutedBrush"];
    }

    public UnraidServerWindow(UnraidServer server, bool pickOnly = false)
    {
        InitializeComponent();
        Server = server;
        _pickOnly = pickOnly;
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        UserBox.Text = server.Username;
        PortBox.Text = server.SshPort.ToString();
        KeyBox.Text = server.PrivateKeyPath ?? "";
        if (server.ProtectedPassword != null) PasswordBox.Password = KeepPassword;
        if (pickOnly)
        {
            Title = $"Pick a VM on {server.Name}";
            SaveButton.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => OnRefresh(this, new RoutedEventArgs());
        }
        else if (!string.IsNullOrEmpty(server.Host))
        {
            Loaded += (_, _) => OnRefresh(this, new RoutedEventArgs());
        }
    }

    private bool ApplyFields()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show(this, "Enter the Unraid host name or IP address.", "Unraid server", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        Server.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text.Trim() : NameBox.Text.Trim();
        Server.Host = HostBox.Text.Trim();
        Server.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim();
        Server.SshPort = int.TryParse(PortBox.Text, out var p) && p > 0 ? p : 22;
        Server.PrivateKeyPath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        if (PasswordBox.Password != KeepPassword) Server.ProtectedPassword = ProfileStore.Protect(PasswordBox.Password);
        Server.NotifyChanged();
        return true;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!ApplyFields()) return;
        RefreshButton.IsEnabled = false;
        StatusText.Text = $"Connecting to {Server.Host} over SSH…";
        try
        {
            var vms = await new UnraidService(Server).ListVmsAsync();
            VmList.ItemsSource = vms.Select(v => new VmRow { Info = v }).ToList();
            StatusText.Text = $"{vms.Count} VM(s), {vms.Count(v => v.IsRunning)} running";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not list VMs";
            MessageBox.Show(this, ex.Message, "Unraid", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnStartVm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VmRow row) return;
        StatusText.Text = $"Starting {row.Name}…";
        try
        {
            await new UnraidService(Server).StartVmAsync(row.Name);
            await Task.Delay(1500);
            OnRefresh(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start VM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnConnectVm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VmRow row) return;
        Pick(row.Info);
    }

    private void OnVmDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VmList.SelectedItem is VmRow row && row.Info.IsRunning) Pick(row.Info);
    }

    private void Pick(UnraidVmInfo vm)
    {
        PickedVm = vm;
        if (_pickOnly)
        {
            DialogResult = true;
            return;
        }
        // Ad-hoc connect from the server browser: reuse an existing profile for this VM or create a temporary one.
        var profile = App.Profiles.Connections.FirstOrDefault(c => c.UnraidServerId == Server.Id && c.UnraidVmName == vm.Name)
                      ?? new ConnectionProfile { Name = vm.Name, Kind = ConnectionKind.UnraidVm, Host = Server.Host, UnraidServerId = Server.Id, UnraidVmName = vm.Name };
        if (!App.Profiles.Connections.Contains(profile))
        {
            App.Profiles.Connections.Add(profile);
            try { App.Profiles.Save(); } catch { }
        }
        var win = new SessionWindow(profile, Server.Host, vm.VncPort, vm.VncPassword);
        win.Show();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!ApplyFields()) return;
        DialogResult = true;
    }
}
