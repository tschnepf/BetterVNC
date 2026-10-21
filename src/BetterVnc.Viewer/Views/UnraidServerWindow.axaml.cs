using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;

namespace BetterVnc.Viewer.Views;

/// <summary>A VM in the server's list.</summary>
public sealed class VmRow
{
    public required UnraidVmInfo Info { get; init; }
    public string Name => Info.Name;
    public string Detail => Info.IsRunning
        ? $"{Info.State} · VNC port {Info.VncPort?.ToString() ?? "?"}{(Info.VncPassword != null ? " · password set" : "")}{(Info.GuestAddress != null ? $" · {Info.GuestAddress}" : "")}"
        : Info.State;
    public string Warnings => Info.Warnings;
    public bool HasWarnings => Info.Warnings.Length > 0;
    public bool IsRunning => Info.IsRunning;
    public bool CanStart => !Info.IsRunning;
    public bool CanConnect => Info.IsRunning && Info.VncPort != null;
}

/// <summary>
/// An Unraid server's SSH access and its VMs: edit the server (closes with true on Save), start a VM, connect to one,
/// or, with <c>pickOnly</c>, pick one for a connection (<see cref="PickedVm"/>).
/// </summary>
public partial class UnraidServerWindow : Window
{
    public UnraidServer Server { get; } = new();
    public UnraidVmInfo? PickedVm { get; private set; }
    private readonly bool _pickOnly;
    private const string KeepPassword = "\u0001keep\u0001";

    public UnraidServerWindow()
    {
        InitializeComponent();
    }

    public UnraidServerWindow(UnraidServer server, bool pickOnly = false) : this()
    {
        Server = server;
        _pickOnly = pickOnly;
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        UserBox.Text = server.Username;
        PortBox.Text = server.SshPort.ToString();
        KeyBox.Text = server.PrivateKeyPath ?? "";
        if (server.ProtectedPassword != null) PasswordBox.Text = KeepPassword;
        if (pickOnly)
        {
            Title = $"Pick a VM on {server.Name}";
            SaveButton.IsVisible = false;
            Opened += (_, _) => OnRefresh(this, new RoutedEventArgs());
        }
        else if (!string.IsNullOrEmpty(server.Host))
        {
            Opened += (_, _) => OnRefresh(this, new RoutedEventArgs());
        }
    }

    private async Task<bool> ApplyFieldsAsync()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            await MessageDialog.ShowAsync(this, "Enter the Unraid host name or IP address.", "Unraid server", MessageButtons.Ok, MessageIcon.Warning);
            return false;
        }
        Server.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text.Trim() : NameBox.Text.Trim();
        Server.Host = HostBox.Text.Trim();
        Server.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim();
        Server.SshPort = int.TryParse(PortBox.Text, out var p) && p > 0 ? p : 22;
        Server.PrivateKeyPath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        if (PasswordBox.Text != KeepPassword) Server.ProtectedPassword = App.Profiles.Protect(PasswordBox.Text ?? "");
        Server.NotifyChanged();
        return true;
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (!await ApplyFieldsAsync()) return;
        RefreshButton.IsEnabled = false;
        StatusText.Text = $"Connecting to {Server.Host} over SSH…";
        try
        {
            var vms = await new UnraidService(Server, App.Profiles.Secrets).ListVmsAsync();
            VmList.ItemsSource = vms.Select(v => new VmRow { Info = v }).ToList();
            StatusText.Text = $"{vms.Count} VM(s), {vms.Count(v => v.IsRunning)} running";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not list VMs";
            await MessageDialog.ShowAsync(this, ex.Message, "Unraid", MessageButtons.Ok, MessageIcon.Warning);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnStartVm(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not VmRow row) return;
        StatusText.Text = $"Starting {row.Name}…";
        try
        {
            await new UnraidService(Server, App.Profiles.Secrets).StartVmAsync(row.Name);
            await Task.Delay(1500);
            OnRefresh(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, ex.Message, "Start VM", MessageButtons.Ok, MessageIcon.Warning);
        }
    }

    private void OnConnectVm(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not VmRow row) return;
        Pick(row.Info);
    }

    private void OnVmDoubleClick(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) == null) return;
        if (VmList.SelectedItem is VmRow row && row.Info.IsRunning) Pick(row.Info);
    }

    private void Pick(UnraidVmInfo vm)
    {
        PickedVm = vm;
        if (_pickOnly)
        {
            Close(true);
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
        var win = new SessionWindow(profile, Server.Host, vm.VncPort, vm.VncPassword, guestAddress: vm.GuestAddress);
        win.Show();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!await ApplyFieldsAsync()) return;
        Close(true);
    }
}
