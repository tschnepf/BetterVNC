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
/// Screen streaming for the Windows agent. Two codecs: H.264 (see <see cref="H264ScreenSession"/>: Desktop
/// Duplication capture, Media Foundation encoder, hardware when the GPU has one) is offered first whenever Media
/// Foundation has an H.264 encoder, which every supported Windows has; motion JPEG (GDI capture, every changed
/// frame a complete JPEG, quality steered to the bitrate) is the fallback that needs no codec at all. In both,
/// unchanged frames are not sent, which is what lets the viewer show the lossless VNC picture while the screen is
/// still.
/// </summary>
public sealed class WindowsScreenSource : IGuestVideoSource
{
    private readonly string? _h264;

    public WindowsScreenSource()
    {
        _h264 = H264Encoder.DescribeAvailable(H264ScreenSession.PreferHardware);
        Codecs = _h264 != null ? new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg } : new[] { AgentVideoCodec.Jpeg };
    }

    public IReadOnlyList<string> Codecs { get; }

    public Action<string>? Log { get; set; }

    public string Description => _h264 != null
        ? $"H.264 with {_h264}, Desktop Duplication capture (GDI fallback); or motion JPEG over GDI"
        : "motion JPEG (software) over GDI; Media Foundation has no H.264 encoder here";

    /// <summary>Both codecs report the pointer: Desktop Duplication's pointer updates, or GDI's current cursor.</summary>
    public bool SupportsCursor => true;

    public IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded) => Start(request, onFrame, onEnded, null);

    public IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded, Action<GuestCursor>? onCursor)
    {
        if (string.Equals(request.Codec, AgentVideoCodec.H264, StringComparison.OrdinalIgnoreCase) && _h264 != null)
        {
            var h264 = new H264ScreenSession(request, onFrame, onEnded, Log, onCursor);
            Log?.Invoke("H.264 stream: " + h264.Description);
            h264.Start();
            return h264;
        }
        if (!string.Equals(request.Codec, AgentVideoCodec.Jpeg, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Codec '{request.Codec}' is not available; this agent encodes {string.Join(" and ", Codecs)}.");
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("No screen to capture (the agent is not running on an interactive desktop).");
        // Fail now, with a message the viewer can show, rather than from the capture thread a moment later.
        using (var probe = new Bitmap(8, 8, PixelFormat.Format32bppRgb))
        using (var g = Graphics.FromImage(probe))
        {
            try { g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, probe.Size, CopyPixelOperation.SourceCopy); }
            catch (Win32Exception ex) { throw new InvalidOperationException("The screen cannot be read right now (locked, or a different session): " + ex.Message); }
        }
        var session = new JpegSession(request, bounds, onFrame, onEnded, Log, onCursor);
        session.Start();
        return session;
    }

    private sealed class JpegSession : IVideoSession
    {
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        private const int MinQuality = 20, MaxQuality = 92, StartQuality = 75;

        private readonly Action<VideoFrame> _onFrame;
        private readonly Action<string?> _onEnded;
        private readonly Action<GuestCursor>? _onCursor;
        private readonly CursorReader? _cursor;
        private readonly Action<string>? _log;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new();
        private volatile int _bitrateKbps;
        private volatile int _maxFps;
        private volatile bool _keyframe = true;
        private volatile bool _disposed;
        private int _quality = StartQuality;
        private Rectangle _bounds;

        public JpegSession(VideoRequest request, Rectangle bounds, Action<VideoFrame> onFrame, Action<string?> onEnded, Action<string>? log, Action<GuestCursor>? onCursor)
        {
            _onFrame = onFrame;
            _onEnded = onEnded;
            _onCursor = onCursor;
            _cursor = onCursor != null ? new CursorReader() : null;
            _bounds = bounds;
            _log = log;
            _bitrateKbps = request.BitrateKbps;
            _maxFps = request.MaxFps;
            Width = bounds.Width;
            Height = bounds.Height;
            _thread = new Thread(Run) { IsBackground = true, Name = "screen capture" };
        }

        public string Codec => AgentVideoCodec.Jpeg;
        public int BitrateKbps => _bitrateKbps;
        public int MaxFps => _maxFps;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string EncoderKind => AgentVideoEncoder.Software;
        public ScreenArea? Area { get { var b = _bounds; return new ScreenArea(b.Left, b.Top, b.Width, b.Height); } }

        public void Start() => _thread.Start();

        public void Set(int? bitrateKbps, int? maxFps)
        {
            if (bitrateKbps is { } b) _bitrateKbps = b;
            if (maxFps is { } f) _maxFps = f;
        }

        public void RequestKeyframe() => _keyframe = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(3000);
        }

        private void Run()
        {
            Bitmap? bmp = null;
            Graphics? g = null;
            byte[]? previous = null, current = null;
            var jpeg = new MemoryStream(1 << 20);
            var clock = Stopwatch.StartNew();
            int failures = 0;
            string? endReason = null;
            try
            {
                while (!_stop.IsSet)
                {
                    long frameStart = clock.ElapsedTicks;
                    var bounds = SystemInformation.VirtualScreen;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        if (_stop.Wait(500)) break;
                        continue;
                    }
                    if (bmp == null || bmp.Width != bounds.Width || bmp.Height != bounds.Height)
                    {
                        g?.Dispose();
                        bmp?.Dispose();
                        bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                        g = Graphics.FromImage(bmp);
                        previous = null;
                        _keyframe = true;
                        Width = bounds.Width;
                        Height = bounds.Height;
                    }
                    _bounds = bounds;

                    try
                    {
                        g!.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                        failures = 0;
                        if (_cursor != null && _cursor.Poll(out bool visible, out var shape))
                            _onCursor!(new GuestCursor(visible, shape?.ToImage()));
                    }
                    catch (Win32Exception ex)
                    {
                        // Lock screen, sign-in screen or a session switch: GDI refuses for a while. Keep trying before giving up.
                        if (++failures >= 20) { endReason = "Screen capture failed: " + ex.Message; break; }
                        if (_stop.Wait(500)) break;
                        continue;
                    }

                    // Anything changed? Identical frames are not sent; the viewer shows lossless VNC meanwhile.
                    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    bool changed;
                    try
                    {
                        int length = data.Stride * data.Height;
                        if (current == null || current.Length != length) current = new byte[length];
                        Marshal.Copy(data.Scan0, current, 0, length);
                        changed = previous == null || previous.Length != length || !current.AsSpan().SequenceEqual(previous);
                    }
                    finally { bmp.UnlockBits(data); }

                    bool forced = _keyframe;
                    int fps = Math.Clamp(_maxFps, 1, 120);
                    double targetInterval = 1.0 / fps;
                    double nextWait;
                    if (changed || forced)
                    {
                        _keyframe = false;
                        (previous, current) = (current, previous);

                        jpeg.SetLength(0);
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
                            bmp.Save(jpeg, JpegCodec, parameters);
                        }
                        long micros = clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                        _onFrame(new VideoFrame(new ReadOnlyMemory<byte>(jpeg.GetBuffer(), 0, (int)jpeg.Length), true, micros, bmp.Width, bmp.Height));

                        // Rate control, per frame. The bitrate is enforced by pacing: a frame of B bytes earns a pause of
                        // B*8/bitrate seconds before the next one (a token bucket). The JPEG quality then steers the trade
                        // between frame rate and sharpness: when the pause is longer than the frame-rate cap allows, the
                        // picture is too big, so quality drops in proportion to the overshoot; when frames are cheap
                        // enough to run at the cap with room to spare, quality creeps back up.
                        double budgetBytesPerSecond = _bitrateKbps * 1000.0 / 8;
                        double earned = jpeg.Length / budgetBytesPerSecond;
                        double ratio = earned / targetInterval;
                        if (ratio > 1.15) _quality = Math.Max(MinQuality, _quality - Math.Clamp((int)Math.Ceiling(8 * Math.Log2(ratio)), 2, 25));
                        else if (ratio < 0.6) _quality = Math.Min(MaxQuality, _quality + 2);
                        nextWait = Math.Max(earned, targetInterval);
                    }
                    else
                    {
                        // Unchanged screen: keep polling at the frame-rate cap, but no faster than 30 times a second.
                        nextWait = Math.Max(targetInterval, 1 / 30.0);
                    }

                    double spent = (clock.ElapsedTicks - frameStart) / (double)Stopwatch.Frequency;
                    int waitMs = (int)Math.Round((nextWait - spent) * 1000);
                    if (waitMs > 0 && _stop.Wait(Math.Min(waitMs, 2000))) break;
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                endReason = "Screen capture stopped: " + ex.Message;
            }
            finally
            {
                g?.Dispose();
                bmp?.Dispose();
            }
            if (!_disposed && endReason != null)
            {
                _log?.Invoke(endReason);
                try { _onEnded(endReason); } catch { }
            }
        }
    }
}
