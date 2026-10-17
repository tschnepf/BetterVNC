namespace BetterVnc.Agent.Protocol;

/// <summary>
/// Input injection on the machine the agent runs on. Implementations are OS specific and are called from the session's
/// read loop, so they should return quickly. Optional: an agent without one simply does not announce
/// <see cref="AgentFeature.Scroll"/>.
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
}
