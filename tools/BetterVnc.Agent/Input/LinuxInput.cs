using BetterVnc.Agent.Clipboards;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Input;

/// <summary>
/// Best-effort Linux scroll injection. X11 has no pixel scroll for synthetic events, so pixels are turned into wheel
/// clicks (48 px, one notch, per click) sent with <c>xdotool click 4/5/6/7</c>. On Wayland <c>ydotool</c> (which needs
/// its daemon, <c>ydotoold</c>) moves the wheel through uinput with the same granularity. Neither tool present: no scroll
/// feature is announced and the viewer keeps using VNC wheel clicks.
/// </summary>
public sealed class LinuxInput : IGuestInput
{
    private const double PixelsPerClick = 3 * 16;

    private readonly bool _ydotool;
    private readonly double _scale;
    private double _restX, _restY;

    private LinuxInput(bool ydotool, double scale)
    {
        _ydotool = ydotool;
        _scale = scale > 0 ? scale : 1.0;
    }

    public string Description => _ydotool ? "ydotool wheel clicks" : "xdotool wheel clicks";

    /// <summary>Picks a tool that fits the session; null when there is none.</summary>
    public static LinuxInput? TryCreate(double scale, Action<string>? log)
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        if (x11 && !wayland && ProcessRunner.Exists("xdotool")) return new LinuxInput(false, scale);
        if (ProcessRunner.Exists("ydotool")) return new LinuxInput(true, scale);
        if (x11 && ProcessRunner.Exists("xdotool")) return new LinuxInput(false, scale); // XWayland: better than nothing
        log?.Invoke("Scroll injection off: install xdotool (X11) or ydotool (Wayland) to enable it.");
        return null;
    }

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        _restY += dy * _scale / PixelsPerClick;
        _restX += dx * _scale / PixelsPerClick;
        int cy = (int)Math.Truncate(_restY);
        int cx = (int)Math.Truncate(_restX);
        _restY -= cy;
        _restX -= cx;
        if (phase == AgentScrollPhase.End) _restX = _restY = 0;
        if (cy == 0 && cx == 0) return;

        if (_ydotool)
        {
            // REL_WHEEL positive = up, REL_HWHEEL positive = right: the same signs as the wire.
            ProcessRunner.Run("ydotool", new[] { "mousemove", "--wheel", "-x", cx.ToString(), "-y", cy.ToString() }, timeoutMs: 2000);
            return;
        }
        // X11 buttons: 4 up, 5 down, 6 left, 7 right.
        if (cy != 0) Click(cy > 0 ? 4 : 5, Math.Abs(cy));
        if (cx != 0) Click(cx > 0 ? 7 : 6, Math.Abs(cx));
    }

    private static void Click(int button, int count) =>
        ProcessRunner.Run("xdotool", new[] { "click", "--repeat", count.ToString(), "--delay", "0", button.ToString() }, timeoutMs: 2000);
}
