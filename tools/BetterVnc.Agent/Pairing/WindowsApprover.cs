using System.Drawing;
using System.Runtime.InteropServices;
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
            Shown += (_, _) => ShowInFront();
        }

        /// <summary>
        /// Puts the dialog in front of every other window, leaving the keyboard with the window the person is typing in.
        /// </summary>
        private void ShowInFront()
        {
            // The agent may be started hidden (Start-Process -WindowStyle Hidden, an older install's hidden launcher);
            // Windows applies that startup style to the first window a process shows, so insist on being visible.
            ShowWindow(Handle, SW_SHOWNORMAL);
            // Windows keeps a process in the background from covering the window the person works in: the dialog opens
            // just behind that window without TopMost, and raising it again, even without activating it, lands it there
            // too (Windows 11), so it sat hidden behind, say, a terminal with only a flashing taskbar button. Only the
            // foreground process may put a window on top, and Windows lets the process that sent the last input take the
            // foreground. So send an empty input (no movement, no buttons; Windows Terminal and PowerToys do the same),
            // take the foreground, go topmost and hand the foreground straight back: the dialog stays in front, and the
            // keyboard stays where the person was typing, so a stray Enter cannot approve the pairing.
            var previous = GetForegroundWindow();
            SendInput(1, new INPUT[1], Marshal.SizeOf<INPUT>());
            bool front = SetForegroundWindow(Handle);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (front)
            {
                _allow.Focus(); // for when the dialog is clicked
                if (previous != IntPtr.Zero && previous != Handle) SetForegroundWindow(previous);
            }
            else
            {
                // Still in the background (input cannot be sent to an elevated window from here, for one): keep the
                // taskbar button flashing until the dialog is clicked.
                var flash = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG };
                FlashWindowEx(ref flash);
            }
        }

        private const int SW_SHOWNORMAL = 1;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        private const uint FLASHW_ALL = 0x3, FLASHW_TIMERNOFG = 0xC;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public MOUSEINPUT mi; } // INPUT_MOUSE (0); the largest member of the union, so the size is right
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);

        private void UpdateAllowText() => _allow.Text = $"Allow ({_secondsLeft})";

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
