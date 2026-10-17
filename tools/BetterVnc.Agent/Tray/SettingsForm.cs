using System.Drawing;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tray;

/// <summary>Settings window opened from the tray icon: port, network, pairing, token, paired viewers, autostart.</summary>
public sealed class SettingsForm : Form
{
    private readonly AgentHost _host;
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly ComboBox _listen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _allowPairing = new() { Text = "Allow new viewers to pair (a dialog on this screen asks you to allow each one)", AutoSize = true };
    private readonly TextBox _token = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly CheckBox _showToken = new() { Text = "Show", AutoSize = true };
    private readonly ListView _viewers = new() { View = View.Details, FullRowSelect = true, HideSelection = false, Height = 130, Width = 500 };
    private readonly CheckBox _autostart = new() { Text = "Start the agent when I sign in", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    public SettingsForm(AgentHost host)
    {
        _host = host;
        Text = "BetterVNC guest agent settings";
        Icon = TrayIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(560, 560);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 4) });
            layout.Controls.Add(control);
        }
        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2) };
            layout.Controls.Add(l);
            layout.SetColumnSpan(l, 2);
        }

        Section("Network");
        Row("Port", _port);
        _listen.Items.AddRange(new object[] { "All networks (0.0.0.0)", "This computer only (127.0.0.1, use with an SSH tunnel)" });
        Row("Listen on", _listen);

        Section("Who may connect");
        layout.Controls.Add(_allowPairing);
        layout.SetColumnSpan(_allowPairing, 2);
        var tokenPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        var generate = new Button { Text = "Generate", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        generate.Click += (_, _) => { _token.Text = AgentAuth.NewToken(); _showToken.Checked = true; };
        clear.Click += (_, _) => _token.Text = "";
        _showToken.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_showToken.Checked;
        tokenPanel.Controls.AddRange(new Control[] { _token, _showToken, generate, clear });
        Row("Token or password", tokenPanel);
        var tokenHint = new Label { Text = "Optional. A viewer that knows this value may connect without pairing. Leave it empty to allow paired viewers only.", AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText };
        layout.Controls.Add(tokenHint);
        layout.SetColumnSpan(tokenHint, 2);

        Section("Paired viewers");
        _viewers.Columns.Add("Name", 170);
        _viewers.Columns.Add("Paired on", 130);
        _viewers.Columns.Add("Last seen", 130);
        _viewers.Columns.Add("From", 100);
        var viewersPanel = new Panel { AutoSize = true, Margin = new Padding(0) };
        var remove = new Button { Text = "Remove", AutoSize = true, Location = new Point(0, 136) };
        remove.Click += (_, _) => RemoveSelected();
        _viewers.Location = new Point(0, 0);
        viewersPanel.Controls.AddRange(new Control[] { _viewers, remove });
        layout.Controls.Add(viewersPanel);
        layout.SetColumnSpan(viewersPanel, 2);

        Section("Startup");
        layout.Controls.Add(_autostart);
        layout.SetColumnSpan(_autostart, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { cancel, save });
        _status.Margin = new Padding(0, 10, 20, 0);
        buttons.Controls.Add(_status);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        LoadFromConfig();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ShowWindow(Handle, 1); // SW_SHOWNORMAL: the process may have been started with a hidden window style (see WindowsApprover)
        SetForegroundWindow(Handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void LoadFromConfig()
    {
        var c = _host.Config;
        _port.Value = Math.Clamp(c.Port, 1, 65535);
        _listen.SelectedIndex = c.Listen == "127.0.0.1" ? 1 : 0;
        _allowPairing.Checked = c.AllowPairing;
        _token.Text = c.Token ?? "";
        _autostart.Checked = Installer.IsAutostartEnabled();
        RefreshViewers();
        _status.Text = _host.StatusLine;
    }

    public void RefreshViewers()
    {
        if (IsDisposed) return;
        _viewers.BeginUpdate();
        _viewers.Items.Clear();
        PairedViewer[] viewers;
        lock (_host.Config.Viewers) viewers = _host.Config.Viewers.ToArray();
        foreach (var v in viewers.OrderByDescending(v => v.LastSeen ?? v.PairedAt))
        {
            var item = new ListViewItem(new[] { v.Name, v.PairedAt.ToString("g"), v.LastSeen?.ToString("g") ?? "never", v.Address ?? "" }) { Tag = v.Id };
            _viewers.Items.Add(item);
        }
        _viewers.EndUpdate();
        _status.Text = _host.StatusLine;
    }

    private void RemoveSelected()
    {
        if (_viewers.SelectedItems.Count == 0) return;
        var item = _viewers.SelectedItems[0];
        if (MessageBox.Show(this, $"Remove \"{item.Text}\"? It will have to pair again before it can use the clipboard.", "Remove paired viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _host.RemoveViewer((string)item.Tag!);
        RefreshViewers();
    }

    private async Task SaveAsync()
    {
        var c = _host.Config;
        c.Port = (int)_port.Value;
        c.Listen = _listen.SelectedIndex == 1 ? "127.0.0.1" : "0.0.0.0";
        c.AllowPairing = _allowPairing.Checked;
        c.Token = string.IsNullOrWhiteSpace(_token.Text) ? null : _token.Text.Trim();
        if (!c.AllowPairing && c.Token == null && c.Viewers.Count == 0)
        {
            MessageBox.Show(this, "With pairing off, no token and no paired viewers, nothing could ever connect. Allow pairing or set a token.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { Installer.SetAutostart(_autostart.Checked, _host.ConfigPath); }
        catch (Exception ex) { MessageBox.Show(this, "Could not change the startup setting: " + ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        Enabled = false;
        await _host.SaveAndRestartAsync();
        Enabled = true;
        if (!_host.IsListening)
        {
            MessageBox.Show(this, _host.StatusLine, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = _host.StatusLine;
            return;
        }
        Close();
    }
}
