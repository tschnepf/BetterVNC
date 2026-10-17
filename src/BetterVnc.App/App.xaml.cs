using System.Windows;
using System.Windows.Threading;
using BetterVnc.App.Models;
using BetterVnc.App.Services;
using BetterVnc.App.Views;

namespace BetterVnc.App;

public partial class App : Application
{
    public static ProfileStore Profiles { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Profiles.Load();

        // "BetterVNC.exe host[:port]" or "vnc://host:port" opens a session straight away (handy for shortcuts).
        if (e.Args.Length > 0 && TryParseTarget(e.Args[0], out var host, out var port))
        {
            var profile = Profiles.Connections.FirstOrDefault(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && c.Port == port)
                          ?? new ConnectionProfile { Name = $"{host}:{port}", Host = host, Port = port };
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new SessionWindow(profile, fullscreen: e.Args.Contains("--fullscreen")).Show();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static bool TryParseTarget(string arg, out string host, out int port)
    {
        host = "";
        port = 5900;
        var s = arg.Trim();
        if (s.StartsWith("vnc://", StringComparison.OrdinalIgnoreCase)) s = s[6..].TrimEnd('/');
        if (s.Length == 0 || s.StartsWith('-')) return false;
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p))
        {
            host = s[..colon];
            port = p;
        }
        else
        {
            host = s;
        }
        return host.Length > 0;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "BetterVNC: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
