using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Input;

/// <summary>
/// Windows scroll injection through <c>SendInput</c> (<c>MOUSEEVENTF_WHEEL</c> / <c>MOUSEEVENTF_HWHEEL</c>) at the current
/// cursor position. The viewer's original wheel delta is replayed when it is present, so a notch on the host is a notch in
/// the guest and the guest's own "lines per notch" setting applies; otherwise pixels are converted at 16 px per line and
/// 3 lines per notch. Fractions are carried over to the next event, the way precision touchpads deliver partial notches.
/// </summary>
public sealed class WindowsInput : IGuestInput
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const double WheelDelta = 120;
    private const double PixelsPerNotch = 3 * 16;

    private readonly double _scale;
    private double _restX, _restY;

    public WindowsInput(double scale = 1.0) => _scale = scale > 0 ? scale : 1.0;

    public string Description => "SendInput";

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
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi; // the largest member of the union, so the struct has the right size
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        // Wire signs match Windows: positive dy = wheel away from the user = up, positive dx = tilt right.
        double wy = (wheelDy ?? dy / PixelsPerNotch * WheelDelta) * _scale;
        double wx = (wheelDx ?? dx / PixelsPerNotch * WheelDelta) * _scale;
        var inputs = new List<INPUT>(2);
        Accumulate(inputs, ref _restY, wy, MOUSEEVENTF_WHEEL);
        Accumulate(inputs, ref _restX, wx, MOUSEEVENTF_HWHEEL);
        if (phase == AgentScrollPhase.End) _restX = _restY = 0; // do not carry a fraction into the next gesture
        if (inputs.Count == 0) return;
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) != inputs.Count)
            throw new Win32Exception();
    }

    private static void Accumulate(List<INPUT> list, ref double rest, double value, uint flag)
    {
        rest += value;
        int whole = (int)Math.Truncate(rest);
        if (whole == 0) return;
        rest -= whole;
        list.Add(new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { mouseData = unchecked((uint)whole), dwFlags = flag } });
    }
}
