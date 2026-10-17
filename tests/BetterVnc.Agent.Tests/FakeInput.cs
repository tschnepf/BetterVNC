using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tests;

/// <summary>Records scroll injections instead of performing them.</summary>
public sealed class FakeInput : IGuestInput
{
    public sealed record Call(double Dx, double Dy, double? WheelDx, double? WheelDy, string? Phase);

    private readonly List<Call> _calls = new();

    public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

    /// <summary>When set, the next call throws (the server must log and carry on).</summary>
    public Exception? ThrowOnce { get; set; }

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        lock (_calls) _calls.Add(new Call(dx, dy, wheelDx, wheelDy, phase));
        if (ThrowOnce is { } ex) { ThrowOnce = null; throw ex; }
    }
}
