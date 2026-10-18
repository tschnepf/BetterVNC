namespace BetterVnc.Media;

/// <summary>
/// Picks the frame rate a screen stream can afford on this CPU. When capturing, converting and encoding a frame takes
/// more than <see cref="DownShare"/> of the frame interval for about a second, the rate steps down (60 → 30 → 20 fps
/// below the rate asked for), leaving the rest of the CPU to the machine whose screen this is; when the work would
/// take less than <see cref="UpShare"/> of the next higher rate's interval for about <see cref="UpWindows"/> seconds,
/// it steps back up. That is the frame-rate half of libwebrtc's BALANCED degradation, which Jump Desktop's host uses.
/// Windows with too few frames (a still screen) say nothing either way.
/// </summary>
public sealed class FrameRateGovernor
{
    public const double DownShare = 0.70, UpShare = 0.45;
    public const int UpWindows = 3, MinFramesPerWindow = 5;
    public const long WindowMicros = 1_000_000;
    private static readonly int[] Steps = { 60, 30, 20 };

    private int[] _ladder = Array.Empty<int>();
    private int _level;
    private long _windowStart = -1;
    private long _workSum;
    private int _frames;
    private int _headroom;

    public FrameRateGovernor(int requestedFps) => Reset(requestedFps);

    /// <summary>The rate asked for, the top of the ladder.</summary>
    public int RequestedFps { get; private set; }
    /// <summary>The rate to stream at now.</summary>
    public int Fps => _ladder[_level];
    public bool SteppedDown => _level > 0;

    /// <summary>Starts over at <paramref name="requestedFps"/> (a new frame-rate cap from the viewer).</summary>
    public void Reset(int requestedFps)
    {
        RequestedFps = Math.Max(1, requestedFps);
        _ladder = new[] { RequestedFps }.Concat(Steps.Where(s => s < RequestedFps)).ToArray();
        _level = 0;
        _windowStart = -1;
        _workSum = 0;
        _frames = 0;
        _headroom = 0;
    }

    /// <summary>
    /// Records the work one frame took (capture, conversion and encoding) at <paramref name="nowMicros"/>. Returns why
    /// the rate changed when it did, null otherwise.
    /// </summary>
    public string? Record(long workMicros, long nowMicros)
    {
        if (_windowStart < 0) _windowStart = nowMicros;
        _workSum += Math.Max(0, workMicros);
        _frames++;
        if (nowMicros - _windowStart < WindowMicros) return null;
        double mean = (double)_workSum / _frames;
        int frames = _frames;
        _windowStart = nowMicros;
        _workSum = 0;
        _frames = 0;
        if (frames < MinFramesPerWindow) return null;

        double interval = 1e6 / Fps;
        if (mean > DownShare * interval && _level < _ladder.Length - 1)
        {
            _level++;
            _headroom = 0;
            return $"a frame takes {mean / 1000:0.0} ms, {mean / interval:0%} of the {interval / 1000:0.0} ms frame interval; stepping down to {Fps} fps";
        }
        if (_level > 0 && mean < UpShare * (1e6 / _ladder[_level - 1]))
        {
            if (++_headroom < UpWindows) return null;
            _level--;
            _headroom = 0;
            return $"a frame takes {mean / 1000:0.0} ms, {mean / (1e6 / Fps):0%} of the {1000.0 / Fps:0.0} ms frame interval; stepping back up to {Fps} fps";
        }
        _headroom = 0;
        return null;
    }
}
