using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BetterVnc.Agent.Protocol;
using BetterVnc.Media;

namespace BetterVnc.Agent.Video;

/// <summary>
/// H.264 screen streaming. Desktop Duplication hands over a frame only when something was drawn (GDI polling
/// stands in when duplication is unavailable, e.g. in an RDP session), the Media Foundation encoder, hardware when
/// the GPU has one, turns it into an Annex B access unit, and the capture thread paces frames to the requested
/// rate. An unchanged screen sends nothing, which keeps the viewer's lossless idle fallback working; a key-frame
/// request encodes the current picture even when nothing changed.
/// </summary>
internal sealed class H264ScreenSession : IVideoSession
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);

    private readonly Action<VideoFrame> _onFrame;
    private readonly Action<string?> _onEnded;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly bool _duplicationWanted;
    private bool _primaryOnly;
    private IScreenGrabber _grabber;
    private H264Encoder _encoder;
    private volatile int _bitrateKbps, _maxFps;
    private volatile bool _keyframe;
    private volatile bool _disposed;

    public H264ScreenSession(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded, Action<string>? log)
    {
        _onFrame = onFrame;
        _onEnded = onEnded;
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
    public string Description => $"{_grabber.Name}, {_encoder.Name} ({EncoderKind})";

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
                var g = new DxgiGrabber(_primaryOnly);
                duplication = true;
                return g;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Desktop Duplication unavailable, capturing with GDI: " + ex.Message);
            }
        }
        return new GdiGrabber(_primaryOnly);
    }

    private H264Encoder CreateEncoder(IScreenGrabber grabber)
    {
        int w = grabber.Width & ~1, h = grabber.Height & ~1;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen to capture (the agent is not running on an interactive desktop).");
        return new H264Encoder(w, h, _maxFps, _bitrateKbps, preferHardware: true);
    }

    private unsafe void Run()
    {
        // Frame pacing needs millisecond waits; Windows gives 15 ms ones unless the process asks for better.
        TimeBeginPeriod(1);
        var clock = Stopwatch.StartNew();
        long Micros() => clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
        var bgra = new byte[_grabber.Width * _grabber.Height * 4];
        var output = new List<EncodedFrame>();
        int appliedBitrate = _bitrateKbps, appliedFps = _maxFps;
        long nextAllowed = 0, captured = 0;
        bool pending = false, first = true;
        string? endReason = null;
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
                }
                long interval = 1_000_000L / Math.Clamp(appliedFps, 1, 120);

                Emit(output);

                long now = Micros();
                int wait = now < nextAllowed ? (int)((nextAllowed - now) / 1000) : pending ? 0 : 20;
                if (!_encoder.WantsInput && wait == 0) wait = 1;
                bool changed;
                try
                {
                    changed = _grabber.TryCapture(Math.Clamp(wait, 0, 50), bgra);
                }
                catch (ScreenChangedException ex)
                {
                    _log?.Invoke("Screen capture restarting: " + ex.Message);
                    if (!RecreateGrabber(ref bgra)) { endReason = "Screen capture failed: " + ex.Message; break; }
                    _keyframe = true;
                    continue;
                }
                if (changed)
                {
                    pending = true;
                    captured = Micros();
                }
                if (!_encoder.WantsInput) continue;

                now = Micros();
                if ((pending || _keyframe || first) && now >= nextAllowed)
                {
                    if (first && !pending)
                    {
                        // The first frame must show the screen as it is, even if nothing moves: ask the grabber once more.
                        if (_stop.Wait(20)) break;
                        continue;
                    }
                    first = false;
                    if (_keyframe)
                    {
                        _keyframe = false;
                        if (!_encoder.ForceKeyframe()) RecreateEncoder();
                    }
                    fixed (byte* p = bgra) _encoder.Encode(p, _grabber.Width * 4, pending ? captured : now);
                    pending = false;
                    nextAllowed = now + interval;
                    Emit(output);
                }
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

    private void Emit(List<EncodedFrame> output)
    {
        _encoder.Pump(output);
        if (output.Count == 0) return;
        foreach (var f in output)
            _onFrame(new VideoFrame(f.Data, f.Keyframe, f.TimestampMicros, _encoder.Width, _encoder.Height));
        output.Clear();
    }

    private void RecreateEncoder()
    {
        var old = _encoder;
        _encoder = CreateEncoder(_grabber);
        try { old.Dispose(); } catch { }
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
}

/// <summary>The capture source went away (lock screen, resolution or session change) and must be recreated.</summary>
internal sealed class ScreenChangedException : Exception
{
    public ScreenChangedException(string message) : base(message) { }
}

/// <summary>A way to get the screen as BGRA pixels and to know whether it changed.</summary>
internal interface IScreenGrabber : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>Waits up to <paramref name="timeoutMs"/> for the screen to change; true when it did and <paramref name="bgra"/> holds the new picture.</summary>
    bool TryCapture(int timeoutMs, byte[] bgra);
}

internal sealed class DxgiGrabber : IScreenGrabber
{
    private readonly DesktopDuplicator _dup;

    public DxgiGrabber(bool primaryOnly) { _dup = DesktopDuplicator.Create(primaryOnly); }

    public string Name => $"Desktop Duplication ({_dup.OutputCount} output{(_dup.OutputCount == 1 ? "" : "s")})";
    public int Width => _dup.Width;
    public int Height => _dup.Height;

    public bool TryCapture(int timeoutMs, byte[] bgra)
    {
        try { return _dup.TryCapture(timeoutMs, bgra); }
        catch (DesktopDuplicationLostException ex) { throw new ScreenChangedException(ex.Message); }
    }

    public void Dispose() => _dup.Dispose();
}

/// <summary>GDI fallback: polls the virtual screen and compares it with the previous picture to find out whether it changed.</summary>
internal sealed class GdiGrabber : IScreenGrabber
{
    private readonly bool _primaryOnly;
    private readonly Rectangle _bounds;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private byte[]? _previous;
    private int _failures;

    private static Rectangle Bounds(bool primaryOnly) => primaryOnly ? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen : SystemInformation.VirtualScreen;

    public GdiGrabber(bool primaryOnly)
    {
        _primaryOnly = primaryOnly;
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

    public bool TryCapture(int timeoutMs, byte[] bgra)
    {
        if (timeoutMs > 0) Thread.Sleep(timeoutMs);
        if (Bounds(_primaryOnly) != _bounds) throw new ScreenChangedException("the screen layout changed");
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
        var data = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int length = _bitmap.Width * _bitmap.Height * 4;
            if (data.Stride == _bitmap.Width * 4) Marshal.Copy(data.Scan0, bgra, 0, length);
            else for (int y = 0; y < _bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * _bitmap.Width * 4, _bitmap.Width * 4);
            bool changed = _previous == null || !bgra.AsSpan(0, length).SequenceEqual(_previous);
            if (changed)
            {
                _previous ??= new byte[length];
                Buffer.BlockCopy(bgra, 0, _previous, 0, length);
            }
            return changed;
        }
        finally { _bitmap.UnlockBits(data); }
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
