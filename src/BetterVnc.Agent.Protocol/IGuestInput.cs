namespace BetterVnc.Agent.Protocol;

/// <summary>
/// Input injection on the machine the agent runs on. Implementations are OS specific and are called from the session's
/// read loop, so they should return quickly. Optional: an agent without one simply does not announce
/// <see cref="AgentFeature.Scroll"/> or <see cref="AgentFeature.Input"/>.
/// </summary>
public interface IGuestInput
{
    /// <summary>
    /// Scrolls at the current pointer position. <paramref name="dx"/>/<paramref name="dy"/> are pixels with the signs
    /// described on <see cref="AgentMessage.Dx"/> and <see cref="AgentMessage.Dy"/>; <paramref name="wheelDx"/>/
    /// <paramref name="wheelDy"/> carry the same movement in Windows wheel units (120 per notch) when the viewer knew
    /// them. <paramref name="phase"/> is one of <see cref="AgentScrollPhase"/> or null.
    /// </summary>
    void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase);

    /// <summary>True when <see cref="Scroll"/> works; the server then announces <see cref="AgentFeature.Scroll"/>.</summary>
    bool SupportsScroll => true;

    /// <summary>True when <see cref="Pointer"/> and <see cref="Key"/> work; the server then announces <see cref="AgentFeature.Input"/>.</summary>
    bool SupportsPointerAndKeys => false;

    /// <summary>
    /// Moves the pointer to (<paramref name="x"/>, <paramref name="y"/>), fractions of <paramref name="area"/> from its
    /// top-left corner (0 is the left or top edge, 1 the right or bottom one), or of the whole desktop when the area is
    /// null, then presses the <see cref="AgentButton"/> bits in <paramref name="pressed"/> and releases those in
    /// <paramref name="released"/> there.
    /// </summary>
    void Pointer(double x, double y, ScreenArea? area, int pressed, int released) => throw new NotSupportedException();

    /// <summary>
    /// Presses or releases a key: <paramref name="scanCode"/> is a set 1 make code with 0xE000 added for extended keys
    /// (0 when the key has none), <paramref name="virtualKey"/> the Windows virtual-key code for that case.
    /// </summary>
    void Key(int scanCode, int virtualKey, bool down) => throw new NotSupportedException();
}

/// <summary>
/// A rectangle of the guest's screen in the OS's own screen coordinates: on Windows virtual-screen pixels, negative for
/// a display left of or above the primary one.
/// </summary>
public readonly record struct ScreenArea(int Left, int Top, int Width, int Height);
