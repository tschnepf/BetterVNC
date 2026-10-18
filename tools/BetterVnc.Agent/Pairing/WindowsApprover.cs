using System.Drawing;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Pairing;

/// <summary>Approval dialog on Windows: a small always-on-top form with a countdown, shown on its own STA thread.</summary>
public sealed class WindowsApprover : IPairingApprover
{
    public Task<bool> ApproveAsync(string viewerName, string viewerId, string peerAddress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new PairingDialog(viewerName, peerAddress, 90);
                using var reg = ct.Register(() => { try { form.BeginInvoke(form.Close); } catch { } });
                var result = form.ShowDialog();
                tcs.TrySetResult(result == DialogResult.OK);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "pairing-dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class PairingDialog : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly Button _allow;
        private int _secondsLeft;

        public PairingDialog(string viewerName, string peerAddress, int seconds)
        {
            _secondsLeft = seconds;
            Text = "BetterVNC guest agent";
            Icon = TrayIcon.Load();
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 170);
            Font = new Font("Segoe UI", 9.75f);

            var text = new Label
            {
                AutoSize = false,
                Location = new Point(16, 14),
                Size = new Size(408, 84),
                Text = $"BetterVNC on {viewerName} ({peerAddress}) asks to use this computer's clipboard (images, files, text) and smooth scrolling through the guest agent.\n\nAllow it? You can remove it later from the agent's tray icon.",
            };
            _allow = new Button { Text = "Allow", Location = new Point(236, 120), Size = new Size(90, 32), DialogResult = DialogResult.OK };
            var deny = new Button { Text = "Deny", Location = new Point(334, 120), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { text, _allow, deny });
            AcceptButton = _allow;
            CancelButton = deny;
            UpdateAllowText();
            _timer.Tick += (_, _) =>
            {
                if (--_secondsLeft <= 0) { DialogResult = DialogResult.Cancel; Close(); return; }
                UpdateAllowText();
            };
            _timer.Start();
            // The agent may be started hidden (LaunchAgent, Start-Process -WindowStyle Hidden, an older install's hidden launcher);
            // Windows applies that startup style to the first window a process shows, so insist on being visible.
            Shown += (_, _) => { ShowWindow(Handle, SW_SHOWNORMAL); SetForegroundWindow(Handle); Activate(); _allow.Focus(); };
        }

        private const int SW_SHOWNORMAL = 1;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void UpdateAllowText() => _allow.Text = $"Allow ({_secondsLeft})";

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
