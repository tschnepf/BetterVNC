using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;
using BetterVnc.Media;

namespace BetterVnc.Agent.Video;

/// <summary>
/// H.264 screen streaming. Desktop Duplication hands over a frame only when something was drawn, with the rectangles
/// that changed (GDI polling with tile comparison stands in when duplication is unavailable, e.g. in an RDP session);
/// only those rectangles are copied and converted to NV12, and the Media Foundation encoder, hardware when the GPU has
/// one, turns the picture into an Annex B access unit. The capture thread paces frames to the requested rate, or to a
/// lower one while this CPU cannot keep up (<see cref="FrameRateGovernor"/>), and keeps at most one frame inside the
/// encoder. An unchanged screen sends nothing, which keeps the viewer's lossless idle fallback working; a key-frame
/// request encodes the current picture even when nothing changed. While frames flow, the time each stage takes goes to
/// the log about once a second.
/// </summary>
internal sealed class H264ScreenSession : IVideoSession
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

    private readonly Action<VideoFrame> _onFrame;
    private readonly Action<string?> _onEnded;
    private readonly Action<GuestCursor>? _onCursor;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly bool _duplicationWanted;
    /// <summary>Capture and conversion time of each frame inside the encoder, waiting for its encode time to be added.</summary>
    private readonly Queue<long> _workBeforeEncode = new();
    private bool _primaryOnly;
    private IScreenGrabber _grabber;
    private H264Encoder _encoder;
    private volatile int _bitrateKbps, _maxFps;
    private volatile bool _keyframe;
    private volatile bool _disposed;

    /// <param name="onCursor">Gets the pointer's shape and visibility as they change (Desktop Duplication reports them, GDI polls); null for none.</param>
    public H264ScreenSession(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded, Action<string>? log, Action<GuestCursor>? onCursor = null)
    {
        _onFrame = onFrame;
        _onEnded = onEnded;
        _onCursor = onCursor;
        _log = log;
        _bitrateKbps = request.BitrateKbps;
        _maxFps = Math.Clamp(request.MaxFps, 1, 120);
        _grabber = CreateGrabber(allowDuplication: true, out _duplicationWanted);
        try { _encoder = CreateEncoder(_grabber); }
        catch (Exception ex) when (!_primaryOnly && _grabber.Width > 0)
        {
            // A multi-monitor desktop can be wider than any H.264 level allows: stream the primary display instead.
            log?.Invoke($"The {_grabber.Width}x{_grabber.Height} desktop cannot be encoded as one picture ({ex.Message}); streaming the primary display only.");
            _grabber.Dispose();
            _primaryOnly = true;
            _grabber = CreateGrabber(allowDuplication: true, out _duplicationWanted);
            try { _encoder = CreateEncoder(_grabber); }
            catch { _grabber.Dispose(); throw; }
        }
        catch { _grabber.Dispose(); throw; }
        Width = _encoder.Width;
        Height = _encoder.Height;
        _thread = new Thread(Run) { IsBackground = true, Name = "screen capture (H.264)" };
    }

    public string Codec => AgentVideoCodec.H264;
    public int BitrateKbps => _bitrateKbps;
    public int MaxFps => _maxFps;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string EncoderKind => _encoder.IsHardware ? AgentVideoEncoder.Hardware : AgentVideoEncoder.Software;
    public string Description => $"{_grabber.Name}, {_encoder.Name} ({EncoderKind}){(Offset is { } at ? $", the primary display at {at.X},{at.Y} of the {SystemInformation.VirtualScreen.Width}x{SystemInformation.VirtualScreen.Height} desktop" : "")}";

    /// <summary>
    /// When only the primary display is streamed: where it sits in the virtual screen, measured from the virtual screen's
    /// top-left corner the way an RFB framebuffer is. Null while the whole desktop is streamed.
    /// </summary>
    public ScreenArea? Area
    {
        get
        {
            var grabber = _grabber;
            return new ScreenArea(grabber.Left, grabber.Top, grabber.Width, grabber.Height);
        }
    }

    public (int X, int Y)? Offset
    {
        get
        {
            if (!_primaryOnly) return null;
            var all = SystemInformation.VirtualScreen;
            var grabber = _grabber;
            return (grabber.Left - all.Left, grabber.Top - all.Top);
        }
    }

    public void Start() => _thread.Start();

    public void Set(int? bitrateKbps, int? maxFps)
    {
        if (bitrateKbps is { } b) _bitrateKbps = b;
        if (maxFps is { } f) _maxFps = Math.Clamp(f, 1, 120);
    }

    public void RequestKeyframe() => _keyframe = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(3000);
        if (!_thread.IsAlive)
        {
            _encoder.Dispose();
            _grabber.Dispose();
        }
    }

    /// <summary>Wider or taller than this and no H.264 encoder will take it (level 5.2); the primary display is used instead.</summary>
    private const int MaxEncodeWidth = 4096, MaxEncodeHeight = 2304;

    private IScreenGrabber CreateGrabber(bool allowDuplication, out bool duplication)
    {
        duplication = false;
        if (!_primaryOnly)
        {
            var all = SystemInformation.VirtualScreen;
            if (all.Width > MaxEncodeWidth || all.Height > MaxEncodeHeight)
            {
                _log?.Invoke($"The {all.Width}x{all.Height} desktop is too large for H.264; streaming the primary display only.");
                _primaryOnly = true;
            }
        }
        if (allowDuplication)
        {
            try
            {
                var g = new DxgiGrabber(_primaryOnly, _onCursor != null);
                duplication = true;
                return g;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Desktop Duplication unavailable, capturing with GDI: " + ex.Message);
            }
        }
        return new GdiGrabber(_primaryOnly, _onCursor != null);
    }

    /// <summary>False with BETTERVNC_SOFTWARE_ENCODER=1: the path a VM without a GPU takes, for trying it on a machine that has one.</summary>
    public static bool PreferHardware { get; } = Environment.GetEnvironmentVariable("BETTERVNC_SOFTWARE_ENCODER") != "1";

    private H264Encoder CreateEncoder(IScreenGrabber grabber)
    {
        int w = grabber.Width & ~1, h = grabber.Height & ~1;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen to capture (the agent is not running on an interactive desktop).");
        return new H264Encoder(w, h, _maxFps, _bitrateKbps, PreferHardware);
    }

    private unsafe void Run()
    {
        // Frame pacing needs millisecond waits; Windows gives 15 ms ones unless the process asks for better.
        TimeBeginPeriod(1);
        var clock = Stopwatch.StartNew();
        long Micros() => clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
        var bgra = new byte[_grabber.Width * _grabber.Height * 4];
        // What changed since the last encoded frame, and what the latest capture changed.
        var pending = new DirtyRegion(_grabber.Width, _grabber.Height);
        var captured = new DirtyRegion(_grabber.Width, _grabber.Height);
        var output = new List<EncodedFrame>();
        var governor = new FrameRateGovernor(_maxFps);
        var stats = new StreamStats();
        int appliedBitrate = _bitrateKbps, appliedFps = _maxFps;
        long nextAllowed = 0, capturedAt = 0, lastSubmit = 0, captureWork = 0;
        bool first = true, lastChanged = false;
        string? endReason = null;
        _log?.Invoke($"Stream pipeline: BGRA to NV12 with {PixelConvert.BgraToNv12Instructions} on up to {_encoder.ConvertThreads} thread(s) of {Environment.ProcessorCount}" +
            (_encoder.WorkerThreads is { } wt ? $", encoder limited to {wt} worker thread(s)" : ""));
        try
        {
            while (!_stop.IsSet)
            {
                // Live re-tuning: bitrate changes in place when the encoder allows it; a new frame-rate cap needs a
                // new encoder because its rate control budgets bits per frame from the rate it was created with.
                if (appliedBitrate != _bitrateKbps)
                {
                    appliedBitrate = _bitrateKbps;
                    if (!_encoder.SetBitrate(appliedBitrate)) RecreateEncoder();
                }
                if (appliedFps != _maxFps)
                {
                    appliedFps = _maxFps;
                    RecreateEncoder();
                    governor.Reset(appliedFps);
                }
                // The governor's steps only pace frames: the encoder keeps its rate control, so a frame keeps its bits.
                long interval = 1_000_000L / Math.Clamp(governor.Fps, 1, 120);

                Emit(output, governor, stats, Micros());
                long now = Micros();
                if (stats.Line(now, governor) is { } line) _log?.Invoke(line);

                if (now < nextAllowed)
                {
                    // Not yet time for the next frame. Nothing is captured meanwhile: Desktop Duplication gathers the
                    // changes and GDI compares with the last picture, so the capture made when it is time has them all.
                    long wait = (nextAllowed - now + 999) / 1000;
                    if (_stop.Wait((int)Math.Clamp(wait, 1, _encoder.InFlight > 0 ? 1 : 50))) break;
                    continue;
                }
                // At most one frame inside an asynchronous encoder: until the previous one is out, don't capture, unless it
                // takes so long that the encoder evidently wants more input before it lets go.
                if (_encoder.IsAsync && _encoder.InFlight > 0 && now - lastSubmit < 2 * interval)
                {
                    if (_stop.Wait(1)) break;
                    continue;
                }
                if (!_encoder.WantsInput)
                {
                    if (_stop.Wait(1)) break;
                    continue;
                }

                bool changed;
                captured.Clear();
                try
                {
                    // Right after a change another one is likely: look without waiting. Otherwise wait for one.
                    changed = _grabber.TryCapture((_keyframe && !first) || lastChanged ? 0 : 20, bgra, captured);
                }
                catch (ScreenChangedException ex)
                {
                    _log?.Invoke("Screen capture restarting: " + ex.Message);
                    if (!RecreateGrabber(ref bgra)) { endReason = "Screen capture failed: " + ex.Message; break; }
                    pending = new DirtyRegion(_grabber.Width, _grabber.Height);
                    captured = new DirtyRegion(_grabber.Width, _grabber.Height);
                    lastChanged = false;
                    _keyframe = true;
                    continue;
                }
                lastChanged = changed;
                captureWork += _grabber.WorkMicros;
                if (_onCursor != null && _grabber.TakeCursor() is { } cursor) _onCursor(cursor);
                if (changed)
                {
                    pending.Add(captured);
                    capturedAt = Micros();
                }
                // The first frame must show the screen as it is, so it waits for a capture; a key frame does not.
                if (pending.IsEmpty && !(_keyframe && !first)) continue;

                now = Micros();
                first = false;
                if (_keyframe)
                {
                    _keyframe = false;
                    if (!_encoder.ForceKeyframe()) RecreateEncoder();
                }
                double dirty = pending.Coverage;
                fixed (byte* p = bgra) _encoder.Encode(p, _grabber.Width * 4, pending, pending.IsEmpty ? now : capturedAt);
                stats.Submitted(captureWork, _encoder.LastConvertMicros, dirty);
                _workBeforeEncode.Enqueue(captureWork + _encoder.LastConvertMicros);
                captureWork = 0;
                pending.Clear();
                lastSubmit = now;
                nextAllowed = now + interval;
                Emit(output, governor, stats, Micros());
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            endReason = "Screen streaming stopped: " + ex.Message;
        }
        finally
        {
            try { _encoder.Dispose(); } catch { }
            try { _grabber.Dispose(); } catch { }
        }
        TimeEndPeriod(1);
        if (!_disposed && endReason != null)
        {
            _log?.Invoke(endReason);
            try { _onEnded(endReason); } catch { }
        }
    }

    private void Emit(List<EncodedFrame> output, FrameRateGovernor governor, StreamStats stats, long nowMicros)
    {
        _encoder.Pump(output);
        if (output.Count == 0) return;
        var at = Offset;
        foreach (var f in output)
        {
            stats.Encoded(f.Data.Length, f.EncodeMicros, f.Keyframe);
            long work = (_workBeforeEncode.TryDequeue(out var before) ? before : 0) + f.EncodeMicros;
            if (governor.Record(work, nowMicros) is { } change) _log?.Invoke("Stream: " + change);
            _onFrame(new VideoFrame(f.Data, f.Keyframe, f.TimestampMicros, _encoder.Width, _encoder.Height, at?.X, at?.Y));
        }
        output.Clear();
    }

    private void RecreateEncoder()
    {
        var old = _encoder;
        _encoder = CreateEncoder(_grabber);
        try { old.Dispose(); } catch { }
        _workBeforeEncode.Clear(); // the old encoder's frames in flight will not come out
        Width = _encoder.Width;
        Height = _encoder.Height;
        _keyframe = false; // a new encoder starts with an IDR frame anyway
    }

    /// <summary>After a desktop switch or mode change: try duplication again for a few seconds, then settle for GDI.</summary>
    private bool RecreateGrabber(ref byte[] bgra)
    {
        try { _grabber.Dispose(); } catch { }
        var deadline = Environment.TickCount64 + 8000;
        while (true)
        {
            try
            {
                _grabber = CreateGrabber(_duplicationWanted && Environment.TickCount64 < deadline, out _);
                break;
            }
            catch (Exception)
            {
                if (Environment.TickCount64 >= deadline + 4000 || _stop.Wait(250)) return false;
            }
        }
        int w = _grabber.Width & ~1, h = _grabber.Height & ~1;
        if (bgra.Length != _grabber.Width * _grabber.Height * 4) bgra = new byte[_grabber.Width * _grabber.Height * 4];
        if (w != _encoder.Width || h != _encoder.Height)
        {
            _log?.Invoke($"Screen size changed to {w}x{h}; restarting the encoder.");
            RecreateEncoder();
        }
        return true;
    }

    /// <summary>Per-stage timings summed over about a second, for one log line.</summary>
    private sealed class StreamStats
    {
        private long _start = -1;
        private int _submitted, _encoded, _keyframes;
        private long _capture, _convert, _encode, _encodeMax, _bytes;
        private double _dirty;
        private TimeSpan _cpuStart;

        public void Submitted(long captureMicros, long convertMicros, double dirtyShare)
        {
            _submitted++;
            _capture += captureMicros;
            _convert += convertMicros;
            _dirty += dirtyShare;
        }

        public void Encoded(int bytes, long encodeMicros, bool keyframe)
        {
            _encoded++;
            _bytes += bytes;
            _encode += encodeMicros;
            _encodeMax = Math.Max(_encodeMax, encodeMicros);
            if (keyframe) _keyframes++;
        }

        /// <summary>A line about the last second or so when frames were encoded in it; null otherwise and while a second is not up.</summary>
        public string? Line(long nowMicros, FrameRateGovernor governor)
        {
            if (_start >= 0 && nowMicros - _start < 1_000_000) return null;
            string? line = null;
            var cpu = Environment.CpuUsage.TotalTime;
            if (_start >= 0 && _encoded > 0 && _submitted > 0)
            {
                double seconds = (nowMicros - _start) / 1e6;
                double cpuShare = (cpu - _cpuStart).TotalSeconds / (seconds * Environment.ProcessorCount);
                string cap = governor.SteppedDown ? $"{governor.Fps} of {governor.RequestedFps}" : $"{governor.Fps}";
                line = $"Stream: {_encoded / seconds:0.0} fps (cap {cap}), capture {_capture / 1000.0 / _submitted:0.00} ms, convert {_convert / 1000.0 / _submitted:0.00} ms, " +
                       $"encode {_encode / 1000.0 / _encoded:0.00} ms (max {_encodeMax / 1000.0:0.0}), {_bytes * 8 / seconds / 1000:0} kbit/s, dirty {_dirty * 100 / _submitted:0.0}%" +
                       (_keyframes > 0 ? $", {_keyframes} key frame(s)" : "") +
                       $", agent CPU {cpuShare:0%} of {Environment.ProcessorCount} logical processors";
            }
            _start = nowMicros;
            _cpuStart = cpu;
            _submitted = _encoded = _keyframes = 0;
            _capture = _convert = _encode = _encodeMax = _bytes = 0;
            _dirty = 0;
            return line;
        }
    }
}

/// <summary>The capture source went away (lock screen, resolution or session change) and must be recreated.</summary>
internal sealed class ScreenChangedException : Exception
{
    public ScreenChangedException(string message) : base(message) { }
}

/// <summary>A way to get the screen as BGRA pixels and to know which parts of it changed.</summary>
internal interface IScreenGrabber : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>Top-left corner of the captured area in virtual-screen coordinates (negative for a display left of or above the primary one).</summary>
    int Left { get; }
    int Top { get; }
    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the screen to change; true when it did. Only the changed parts are
    /// written into <paramref name="bgra"/>, which must be the buffer passed the time before (it holds the rest of the
    /// picture), and they are added to <paramref name="changed"/>. A new buffer gets the whole picture.
    /// </summary>
    bool TryCapture(int timeoutMs, byte[] bgra, DirtyRegion changed);
    /// <summary>Time the last <see cref="TryCapture"/> spent reading and copying pixels, without the wait.</summary>
    long WorkMicros { get; }
    /// <summary>The pointer when it changed since the last call (the first call reports it as it is); null otherwise, or when it is not followed.</summary>
    GuestCursor? TakeCursor();
}

internal sealed class DxgiGrabber : IScreenGrabber
{
    private readonly DesktopDuplicator _dup;
    private bool _initialShapeDone;

    public DxgiGrabber(bool primaryOnly, bool trackPointer)
    {
        _dup = DesktopDuplicator.Create(primaryOnly);
        _dup.TrackPointer = trackPointer;
    }

    public string Name => $"Desktop Duplication ({_dup.OutputCount} output{(_dup.OutputCount == 1 ? "" : "s")})";
    public int Width => _dup.Width;
    public int Height => _dup.Height;
    public int Left => _dup.Left;
    public int Top => _dup.Top;
    public long WorkMicros => _dup.LastCopyMicros;

    public bool TryCapture(int timeoutMs, byte[] bgra, DirtyRegion changed)
    {
        try { return _dup.TryCapture(timeoutMs, bgra, changed); }
        catch (DesktopDuplicationLostException ex) { throw new ScreenChangedException(ex.Message); }
    }

    public GuestCursor? TakeCursor()
    {
        if (!_dup.TrackPointer) return null;
        bool changed = _dup.TakePointerUpdate(out bool visible, out var shape);
        if (!_initialShapeDone)
        {
            // The compositor describes the pointer when it changes, which may take a while: until it has, the shape
            // showing now comes from GDI, once.
            _initialShapeDone = true;
            if (shape == null && new CursorReader().Poll(out bool showing, out var current) && current != null)
            {
                shape = current;
                if (!changed) visible = showing;
                changed = true;
            }
        }
        return changed ? new GuestCursor(visible, shape?.ToImage()) : null;
    }

    public void Dispose() => _dup.Dispose();
}

/// <summary>
/// GDI fallback: polls the virtual screen and compares it with the previous picture in 64×64 tiles; the tiles that
/// differ are the changed regions, and only they are copied.
/// </summary>
internal sealed class GdiGrabber : IScreenGrabber
{
    private const int Tile = 64;
    private readonly bool _primaryOnly;
    private readonly Rectangle _bounds;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly CursorReader? _cursor;
    private byte[]? _picture;
    private int _failures;

    private static Rectangle Bounds(bool primaryOnly) => primaryOnly ? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen : SystemInformation.VirtualScreen;

    public GdiGrabber(bool primaryOnly, bool trackPointer)
    {
        _primaryOnly = primaryOnly;
        _cursor = trackPointer ? new CursorReader() : null;
        _bounds = Bounds(primaryOnly);
        if (_bounds.Width <= 0 || _bounds.Height <= 0) throw new InvalidOperationException("No screen to capture (the agent is not running on an interactive desktop).");
        _bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppRgb);
        _graphics = Graphics.FromImage(_bitmap);
        try { _graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, new Size(8, 8), CopyPixelOperation.SourceCopy); }
        catch (Win32Exception ex)
        {
            _graphics.Dispose();
            _bitmap.Dispose();
            throw new InvalidOperationException("The screen cannot be read right now (locked, or a different session): " + ex.Message);
        }
    }

    public string Name => _primaryOnly ? "GDI capture (primary display)" : "GDI capture";
    public int Width => _bounds.Width;
    public int Height => _bounds.Height;
    public int Left => _bounds.Left;
    public int Top => _bounds.Top;
    public long WorkMicros { get; private set; }

    public unsafe bool TryCapture(int timeoutMs, byte[] bgra, DirtyRegion changed)
    {
        WorkMicros = 0;
        if (timeoutMs > 0) Thread.Sleep(timeoutMs);
        if (Bounds(_primaryOnly) != _bounds) throw new ScreenChangedException("the screen layout changed");
        long start = Stopwatch.GetTimestamp();
        try
        {
            _graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);
            _failures = 0;
        }
        catch (Win32Exception ex)
        {
            // Lock screen, sign-in screen or a session switch: GDI refuses for a while.
            if (++_failures >= 20) throw new ScreenChangedException("GDI capture keeps failing: " + ex.Message);
            return false;
        }
        int width = _bitmap.Width, height = _bitmap.Height;
        var data = _bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // A buffer we have not filled before holds no previous picture to compare with: everything is new.
            bool all = !ReferenceEquals(bgra, _picture);
            _picture = bgra;
            bool any = false;
            byte* src = (byte*)data.Scan0;
            fixed (byte* dst = bgra)
            {
                for (int ty = 0; ty < height; ty += Tile)
                {
                    int th = Math.Min(Tile, height - ty);
                    for (int tx = 0; tx < width; tx += Tile)
                    {
                        int bytes = Math.Min(Tile, width - tx) * 4;
                        bool differs = all;
                        for (int row = ty; row < ty + th && !differs; row++)
                            differs = !new ReadOnlySpan<byte>(src + (long)row * data.Stride + tx * 4, bytes)
                                .SequenceEqual(new ReadOnlySpan<byte>(dst + ((long)row * width + tx) * 4, bytes));
                        if (!differs) continue;
                        for (int row = ty; row < ty + th; row++)
                            Buffer.MemoryCopy(src + (long)row * data.Stride + tx * 4, dst + ((long)row * width + tx) * 4, bytes, bytes);
                        changed.Add(tx, ty, bytes / 4, th);
                        any = true;
                    }
                }
            }
            return any;
        }
        finally
        {
            _bitmap.UnlockBits(data);
            WorkMicros = (Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency;
        }
    }

    public GuestCursor? TakeCursor() =>
        _cursor != null && _cursor.Poll(out bool visible, out var shape) ? new GuestCursor(visible, shape?.ToImage()) : null;

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}

internal static class PointerShapeExtensions
{
    public static CursorImage ToImage(this PointerShape shape) => new(shape.Width, shape.Height, shape.HotX, shape.HotY, shape.Bgra);
}
