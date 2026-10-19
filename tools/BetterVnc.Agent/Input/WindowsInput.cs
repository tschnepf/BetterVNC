using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Input;

/// <summary>
/// Windows input injection through <c>SendInput</c>.
/// <para>
/// Scroll (<c>MOUSEEVENTF_WHEEL</c> / <c>MOUSEEVENTF_HWHEEL</c>) applies at the current cursor position. The viewer's
/// original wheel delta is replayed when it is present, so a notch on the host is a notch in the guest and the guest's
/// own "lines per notch" setting applies; otherwise pixels are converted at 16 px per line and 3 lines per notch.
/// Fractions are carried over to the next event, the way precision touchpads deliver partial notches.
/// </para>
/// <para>
/// Pointer and keys, for viewers that control this machine without VNC: the pointer moves in absolute coordinates over
/// the whole virtual desktop (<c>MOUSEEVENTF_VIRTUALDESK</c>), so every display is reachable, and keys go in as scan
/// codes, so the guest's own keyboard layout turns them into characters exactly as for a physical keyboard. The agent
/// runs per-monitor DPI aware, so the coordinates are physical pixels, the same ones the capture uses.
/// </para>
/// </summary>
public sealed class WindowsInput : IGuestInput
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint XBUTTON1 = 1, XBUTTON2 = 2;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const double WheelDelta = 120;
    private const double PixelsPerNotch = 3 * 16;

    private readonly double _scale;
    private volatile bool _scroll, _pointerAndKeys;
    private double _restX, _restY;

    /// <param name="scale">Multiplier for scroll distances.</param>
    /// <param name="scroll">Take scroll messages (smooth scrolling next to VNC, and the wheel without VNC).</param>
    /// <param name="pointerAndKeys">Take pointer and key messages: a viewer can control this machine without VNC.</param>
    public WindowsInput(double scale = 1.0, bool scroll = true, bool pointerAndKeys = true)
    {
        _scale = scale > 0 ? scale : 1.0;
        _scroll = scroll;
        _pointerAndKeys = pointerAndKeys;
    }

    public string Description => "SendInput: " + (_scroll && _pointerAndKeys ? "scroll, pointer and keys" : _scroll ? "scroll only" : "pointer and keys (no smooth scroll)");

    /// <summary>Settable, so a settings change applies when the server restarts (it reads these for every welcome).</summary>
    public bool SupportsScroll { get => _scroll; set => _scroll = value; }
    public bool SupportsPointerAndKeys { get => _pointerAndKeys; set => _pointerAndKeys = value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // the largest member, so the union has the right size
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) != inputs.Count)
            throw new Win32Exception(); // access denied while the secure desktop (UAC, lock screen) has the input
    }

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
        new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        if (!_scroll) return;
        // Wire signs match Windows: positive dy = wheel away from the user = up, positive dx = tilt right.
        double wy = (wheelDy ?? dy / PixelsPerNotch * WheelDelta) * _scale;
        double wx = (wheelDx ?? dx / PixelsPerNotch * WheelDelta) * _scale;
        var inputs = new List<INPUT>(2);
        Accumulate(inputs, ref _restY, wy, MOUSEEVENTF_WHEEL);
        Accumulate(inputs, ref _restX, wx, MOUSEEVENTF_HWHEEL);
        if (phase == AgentScrollPhase.End) _restX = _restY = 0; // do not carry a fraction into the next gesture
        Send(inputs);
    }

    private static void Accumulate(List<INPUT> list, ref double rest, double value, uint flag)
    {
        rest += value;
        int whole = (int)Math.Truncate(rest);
        if (whole == 0) return;
        rest -= whole;
        list.Add(Mouse(flag, data: unchecked((uint)whole)));
    }

    public void Pointer(double x, double y, ScreenArea? area, int pressed, int released)
    {
        if (!_pointerAndKeys) return;
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("There is no desktop to point at.");
        var a = area ?? new ScreenArea(left, top, width, height);
        // A position in pixels (the caller aims at the middle of one), then in the 0..65535 range that absolute
        // coordinates span across the virtual desktop.
        double px = a.Left + x * a.Width, py = a.Top + y * a.Height;
        int nx = (int)Math.Clamp(Math.Round((px - left) * 65536.0 / width), 0, 65535);
        int ny = (int)Math.Clamp(Math.Round((py - top) * 65536.0 / height), 0, 65535);
        var inputs = new List<INPUT>(3) { Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny) };
        AddButtons(inputs, pressed, down: true);
        AddButtons(inputs, released, down: false);
        Send(inputs);
    }

    private static void AddButtons(List<INPUT> inputs, int buttons, bool down)
    {
        if ((buttons & AgentButton.Left) != 0) inputs.Add(Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP));
        if ((buttons & AgentButton.Right) != 0) inputs.Add(Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP));
        if ((buttons & AgentButton.Middle) != 0) inputs.Add(Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP));
        if ((buttons & AgentButton.Back) != 0) inputs.Add(Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: XBUTTON1));
        if ((buttons & AgentButton.Forward) != 0) inputs.Add(Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: XBUTTON2));
    }

    public void Key(int scanCode, int virtualKey, bool down)
    {
        if (!_pointerAndKeys) return;
        var ki = new KEYBDINPUT();
        int prefix = scanCode >> 8;
        if (scanCode != 0 && (prefix == 0 || prefix == 0xE0))
        {
            // The scan code decides; Windows looks up the virtual key in the layout of the window that gets the key.
            ki.wScan = (ushort)(scanCode & 0xFF);
            ki.dwFlags = KEYEVENTF_SCANCODE | (prefix == 0xE0 ? KEYEVENTF_EXTENDEDKEY : 0);
        }
        else if (virtualKey is > 0 and <= 0xFE)
        {
            ki.wVk = (ushort)virtualKey; // Pause, and keys the viewer knows no scan code for
        }
        else return;
        if (!down) ki.dwFlags |= KEYEVENTF_KEYUP;
        Send(new List<INPUT>(1) { new() { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = ki } } });
    }
}
