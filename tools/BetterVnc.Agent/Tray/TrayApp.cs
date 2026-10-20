using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tray;

/// <summary>Notification-area icon for the Windows agent: status, settings, log, quit. Runs the message loop on the calling (STA) thread.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AgentHost _host;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _viewers;
    private readonly SynchronizationContext _sync;
    private SettingsForm? _settings;

    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static void Run(AgentHost host)
    {
        FreeConsole(); // attached to the terminal it was started from (Program.AttachToParentConsole): let it go
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        using var app = new TrayApp(host);
        Application.Run(app);
    }

    private TrayApp(AgentHost host)
    {
        _host = host;
        _sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_sync);

        _status = new ToolStripMenuItem { Enabled = false };
        _viewers = new ToolStripMenuItem { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"BetterVNC guest agent {host.Version}") { Enabled = false });
        menu.Items.Add(_status);
        menu.Items.Add(_viewers);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = TrayIcon.Load(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _icon.BalloonTipClicked += (_, _) => ShowSettings();

        host.Changed += () => _sync.Post(_ => Refresh(), null);
        host.Paired += v => _sync.Post(_ => _icon.ShowBalloonTip(5000, "BetterVNC guest agent", $"Paired with {v.Name}. Its clipboard now syncs with this computer.", ToolTipIcon.Info), null);
        Refresh();
        if (!host.IsListening)
            _icon.ShowBalloonTip(8000, "BetterVNC guest agent", host.StatusLine + ". Open Settings to change the port.", ToolTipIcon.Warning);
    }

    private void Refresh()
    {
        _status.Text = _host.StatusLine;
        _viewers.Text = _host.ViewersLine;
        var tip = $"BetterVNC guest agent: {_host.StatusLine}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        _settings?.RefreshViewers();
    }

    private void ShowSettings()
    {
        if (_settings != null && !_settings.IsDisposed)
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(_host);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_host.LogPath)) File.WriteAllText(_host.LogPath, "");
            Process.Start(new ProcessStartInfo(_host.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Open log", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Quit()
    {
        _icon.Visible = false;
        try { _host.DisposeAsync().AsTask().Wait(3000); } catch { }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
