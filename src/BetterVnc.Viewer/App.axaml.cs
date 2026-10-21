using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BetterVnc.Platform;
using BetterVnc.Platform.Clipboards;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Views;

namespace BetterVnc.Viewer;

public partial class App : Application
{
    public static ProfileStore Profiles { get; } = new(new DpapiSecretStore());

    /// <summary>The Windows clipboard, shared by every session (one STA thread, one change listener).</summary>
    public static WindowsClipboard LocalClipboard { get; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            string? loadError = null;
            try
            {
                Profiles.Load();
            }
            catch (Exception ex)
            {
                loadError = $"Could not read {Profiles.FilePath}:\n{ex.Message}";
            }

            // "BetterVNC.exe host[:port]" or "vnc://host:port" opens a session straight away (handy for shortcuts).
            var args = desktop.Args ?? Array.Empty<string>();
            Window first;
            if (args.Length > 0 && TryParseTarget(args[0], out var host, out var port))
            {
                var profile = Profiles.Connections.FirstOrDefault(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && c.Port == port)
                              ?? new ConnectionProfile { Name = $"{host}:{port}", Host = host, Port = port };
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                first = new SessionWindow(profile, fullscreen: args.Contains("--fullscreen"));
            }
            else
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                first = new MainWindow();
            }
            desktop.MainWindow = first;
            if (loadError != null)
                first.Opened += (_, _) => _ = MessageDialog.ShowAsync(first, loadError, "BetterVNC", MessageButtons.Ok, MessageIcon.Warning);
        }
        base.OnFrameworkInitializationCompleted();
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

    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var owner = (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive)
                    ?? (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        _ = MessageDialog.ShowAsync(owner, e.Exception.ToString(), "BetterVNC: unexpected error", MessageButtons.Ok, MessageIcon.Error);
        e.Handled = true;
    }
}
