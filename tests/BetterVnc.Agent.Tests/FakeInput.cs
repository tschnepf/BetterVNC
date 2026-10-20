using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tests;

/// <summary>Records injections instead of performing them.</summary>
public sealed class FakeInput : IGuestInput
{
    public sealed record Call(double Dx, double Dy, double? WheelDx, double? WheelDy, string? Phase);
    public sealed record PointerCall(double X, double Y, ScreenArea? Area, int Pressed, int Released);
    public sealed record KeyCall(int ScanCode, int VirtualKey, bool Down);

    private readonly List<Call> _calls = new();
    private readonly List<object> _input = new();

    /// <summary>Scroll calls, in order.</summary>
    public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }
    /// <summary>Pointer and key calls, in the order they happened.</summary>
    public IReadOnlyList<object> Input { get { lock (_input) return _input.ToList(); } }

    /// <summary>When set, the next call throws (the server must log and carry on).</summary>
    public Exception? ThrowOnce { get; set; }

    /// <summary>Announce pointer and keys (off by default, like an agent that only scrolls).</summary>
    public bool PointerAndKeys { get; init; }

    public bool SupportsPointerAndKeys => PointerAndKeys;

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        lock (_calls) _calls.Add(new Call(dx, dy, wheelDx, wheelDy, phase));
        if (ThrowOnce is { } ex) { ThrowOnce = null; throw ex; }
    }

    public void Pointer(double x, double y, ScreenArea? area, int pressed, int released)
    {
        lock (_input) _input.Add(new PointerCall(x, y, area, pressed, released));
        if (ThrowOnce is { } ex) { ThrowOnce = null; throw ex; }
    }

    public void Key(int scanCode, int virtualKey, bool down)
    {
        lock (_input) _input.Add(new KeyCall(scanCode, virtualKey, down));
        if (ThrowOnce is { } ex) { ThrowOnce = null; throw ex; }
    }
}
