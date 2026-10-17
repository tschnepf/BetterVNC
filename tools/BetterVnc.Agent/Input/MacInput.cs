using System.Runtime.InteropServices;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Input;

/// <summary>
/// macOS scroll injection through CoreGraphics: <c>CGEventCreateScrollWheelEvent2</c> with pixel units, posted to the HID
/// event tap at the current pointer position. Pixel events are what a trackpad produces, so apps scroll smoothly instead of
/// jumping a line per wheel click the way they do for the RFB wheel buttons that Screen Sharing turns into line events.
/// With <see cref="Phases"/> on, the gesture phases the viewer sends are forwarded as well, which lets AppKit treat the
/// stream like a trackpad gesture (rubber banding, momentum handling).
/// <para>
/// Posting events needs the Accessibility permission (System Settings, Privacy &amp; Security, Accessibility) for the
/// agent executable. Without it CoreGraphics silently drops the events; the first scroll therefore checks
/// <c>AXIsProcessTrusted</c>, logs instructions and asks macOS to show its prompt once.
/// </para>
/// </summary>
public sealed class MacInput : IGuestInput, IDisposable
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint kCGScrollEventUnitPixel = 0;
    private const uint kCGHIDEventTap = 0;
    private const int kCGEventSourceStateHIDSystemState = 1;
    private const uint kCGScrollWheelEventIsContinuous = 88;
    private const uint kCGScrollWheelEventScrollPhase = 99;
    private const long kCGScrollPhaseBegan = 1, kCGScrollPhaseChanged = 2, kCGScrollPhaseEnded = 4;

    [DllImport(CoreGraphics)] private static extern IntPtr CGEventSourceCreate(int stateId);
    [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CoreGraphics)] private static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(ApplicationServices)] private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    private readonly double _scale;
    private readonly bool _invert;
    private readonly IntPtr _source;
    private double _restX, _restY;
    private bool _trustChecked;
    private bool _disposed;

    /// <param name="scale">Multiplier for the pixel deltas (1.0 = 48 px per notch at the Windows default of 3 lines).</param>
    /// <param name="invert">Flip both axes, for setups where macOS applies "natural" direction to the synthetic events as well.</param>
    /// <param name="phases">Forward begin/continue/end as CGEvent scroll phases.</param>
    public MacInput(double scale = 1.0, bool invert = false, bool phases = true)
    {
        _scale = scale > 0 ? scale : 1.0;
        _invert = invert;
        Phases = phases;
        _source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState); // may be null; CoreGraphics then uses a default source
    }

    public bool Phases { get; }
    public string Description => "CoreGraphics pixel scroll" + (Phases ? " with phases" : "");
    public Action<string>? Log { get; set; }

    /// <summary>True when macOS lets this process post events. Shows the system prompt once when <paramref name="prompt"/> is set.</summary>
    public bool IsTrusted(bool prompt)
    {
        try
        {
            if (AXIsProcessTrusted()) return true;
            if (!prompt) return false;
            IntPtr options = IntPtr.Zero;
            try
            {
                var appServices = NativeLibrary.Load(ApplicationServices);
                var cf = NativeLibrary.Load(CoreFoundation);
                var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(appServices, "kAXTrustedCheckOptionPrompt"));
                var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
                var keyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
                var valueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
                options = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { yes }, 1, keyCallbacks, valueCallbacks);
                return AXIsProcessTrustedWithOptions(options);
            }
            finally { if (options != IntPtr.Zero) CFRelease(options); }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Accessibility check failed: " + ex.Message);
            return false;
        }
    }

    public void Scroll(double dx, double dy, double? wheelDx, double? wheelDy, string? phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_trustChecked)
        {
            _trustChecked = true;
            if (!IsTrusted(prompt: true))
                Log?.Invoke("Scroll injection needs the Accessibility permission: System Settings > Privacy & Security > Accessibility, " +
                            "add bettervnc-agent (and the terminal or launchd job that starts it). Events are dropped until then.");
        }

        double sign = _invert ? -1 : 1;
        _restY += dy * _scale * sign;
        _restX += dx * _scale * sign;
        int py = (int)Math.Truncate(_restY);
        int px = (int)Math.Truncate(_restX);
        _restY -= py;
        _restX -= px;
        // CoreGraphics: wheel1 is vertical with positive = up (same as the wire); wheel2 is horizontal with positive = left,
        // so the wire's positive-right dx is negated.
        int w1 = py, w2 = -px;

        if (!Phases)
        {
            if (w1 != 0 || w2 != 0) Post(w1, w2, null);
            return;
        }
        switch (phase)
        {
            case AgentScrollPhase.Begin:
                Post(0, 0, kCGScrollPhaseBegan);
                if (w1 != 0 || w2 != 0) Post(w1, w2, kCGScrollPhaseChanged);
                break;
            case AgentScrollPhase.End:
                if (w1 != 0 || w2 != 0) Post(w1, w2, kCGScrollPhaseChanged);
                Post(0, 0, kCGScrollPhaseEnded);
                _restX = _restY = 0;
                break;
            default:
                if (w1 != 0 || w2 != 0) Post(w1, w2, kCGScrollPhaseChanged);
                break;
        }
    }

    private void Post(int wheel1, int wheel2, long? phase)
    {
        var evt = CGEventCreateScrollWheelEvent2(_source, kCGScrollEventUnitPixel, 2, wheel1, wheel2, 0);
        if (evt == IntPtr.Zero) throw new InvalidOperationException("CGEventCreateScrollWheelEvent2 returned null.");
        try
        {
            CGEventSetIntegerValueField(evt, kCGScrollWheelEventIsContinuous, 1);
            if (phase is { } p) CGEventSetIntegerValueField(evt, kCGScrollWheelEventScrollPhase, p);
            CGEventPost(kCGHIDEventTap, evt);
        }
        finally { CFRelease(evt); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source != IntPtr.Zero) CFRelease(_source);
    }
}
