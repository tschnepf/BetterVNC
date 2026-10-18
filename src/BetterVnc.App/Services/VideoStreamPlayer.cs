using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.App.Controls;
using BetterVnc.App.Models;
using BetterVnc.Media;
using BetterVnc.Protocol;

namespace BetterVnc.App.Services;

/// <summary>
/// Plays the guest agent's video stream into a <see cref="VncView"/> while the RFB connection goes on carrying input,
/// cursor shapes and resizes at a trickle. Frames are decoded off the UI thread with a latest-frame-wins policy for
/// what is shown: motion JPEG frames stand alone, so a slow decoder simply skips to the newest; H.264 delta frames
/// all go through the decoder (they are each other's reference) but only the newest picture is shown.
/// Where it can, H.264 is decoded on the GPU (DXVA, on the view's Direct3D device) and presented from there by the
/// view's swap chain, never touching the CPU or the UI thread; otherwise the decoder runs in software and the view
/// converts on the GPU, or, without Direct3D, the frame is converted here and drawn through WPF.
/// When the agent sends nothing for a moment (the screen is still) the view switches back to the RFB framebuffer and
/// a full lossless update is requested, so a quiet desktop is always shown pixel-exact; the first new frame switches
/// back to the stream.
/// </summary>
public sealed class VideoStreamPlayer : IDisposable
{
    /// <summary>No frame for this long means the screen is still: show the lossless RFB picture instead.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMilliseconds(700);
    /// <summary>RFB update pacing while the stream carries the picture: enough for cursor shapes and resizes, little pixel traffic.</summary>
    public static readonly TimeSpan RfbIntervalWhileStreaming = TimeSpan.FromSeconds(1);

    private readonly AgentClient _agent;
    private readonly VncClient _client;
    private readonly VncView _view;
    private readonly Dispatcher _dispatcher;
    private readonly Channel<AgentFrame> _frames;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _idleTimer;
    private readonly WicJpegDecoder _jpeg = new();
    private readonly ConcurrentBag<byte[]> _pool = new();
    private Task? _decodeLoop;
    private int _generation;
    private volatile bool _lostFrames;
    private long _lastKeyframeRequest;
    private bool _disposed;

    // statistics (read from the UI thread, written from the decode thread; slightly stale is fine)
    private long _bytesReceived, _framesReceived, _framesDecoded, _framesSkipped;
    private long _bytesAtRate;
    private DateTime _rateTime = DateTime.UtcNow;

    public VideoStreamPlayer(AgentClient agent, VncClient client, VncView view, Dispatcher dispatcher)
    {
        _agent = agent;
        _client = client;
        _view = view;
        _dispatcher = dispatcher;
        // Room for a burst of H.264 delta frames; when even that overflows the oldest go and a key frame is requested.
        _frames = Channel.CreateBounded<AgentFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => { Interlocked.Increment(ref _framesSkipped); _lostFrames = true; });
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleAfter };
        _idleTimer.Tick += (_, _) => OnIdle();
        _agent.StreamFrame += OnFrame;
        _agent.StreamStopped += OnAgentStopped;
    }

    /// <summary>The stream mode in effect; null while the picture comes from RFB alone.</summary>
    public PictureModes.Info? Mode { get; private set; }
    public bool IsStreaming => Mode != null;
    public AgentConnection.VideoStartInfo? Info => _agent.StreamInfo;
    /// <summary>True while the screen is still and the lossless RFB picture is shown in place of the stream.</summary>
    public bool IdleFallbackActive { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public long FramesSkipped => Interlocked.Read(ref _framesSkipped);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    /// <summary>How the current stream is decoded and shown, for the status bar ("DXVA → GPU", "software → WPF"); null before the first picture.</summary>
    public string? DecodePath { get; private set; }

    /// <summary>Short status for the status bar.</summary>
    public event Action<string>? Message;
    /// <summary>The stream ended without <see cref="StopAsync"/> (agent gone, capture failed); the player is back on RFB. On the dispatcher.</summary>
    public event Action<string?>? Ended;

    /// <summary>Bytes per second since the previous call (call about once a second).</summary>
    public double TakeRate()
    {
        var now = DateTime.UtcNow;
        var bytes = BytesReceived;
        double seconds = Math.Max(0.001, (now - _rateTime).TotalSeconds);
        double rate = (bytes - _bytesAtRate) / seconds;
        _bytesAtRate = bytes;
        _rateTime = now;
        return rate;
    }

    /// <summary>Starts (or re-tunes) the stream for a stream mode. Throws when the agent refuses; the picture then stays on RFB.</summary>
    public async Task StartAsync(PictureModes.Info mode)
    {
        if (!mode.IsStream) throw new ArgumentException("Not a stream mode.", nameof(mode));
        if (IsStreaming)
        {
            await _agent.SetStreamAsync(mode.BitrateKbps, mode.MaxFps).ConfigureAwait(true);
            Mode = mode;
            Message?.Invoke($"Stream re-tuned to {mode.BitrateKbps / 1000.0:0.#} Mbit/s, {mode.MaxFps} fps max");
            return;
        }
        // The decoder of this stream needs the view's Direct3D device to decode on the GPU; set it up before frames come.
        _view.EnableGpuVideo();
        var info = await _agent.StartStreamAsync(mode.BitrateKbps, mode.MaxFps).ConfigureAwait(true);
        Mode = mode;
        DecodePath = null;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _framesDecoded, 0);
        Interlocked.Exchange(ref _framesSkipped, 0);
        _bytesAtRate = 0;
        _decodeLoop ??= Task.Run(() => DecodeLoopAsync(_cts.Token));
        // RFB keeps running at a trickle: cursor shapes and resizes still arrive, pixels mostly come from the stream.
        _client.UpdateInterval = RfbIntervalWhileStreaming;
        IdleFallbackActive = false;
        _idleTimer.Start();
        string where = info.OffsetX is { } ox ? $" (the primary display, at {ox},{info.OffsetY ?? 0} of the desktop; the other displays stay on VNC)" : "";
        Message?.Invoke($"Streaming {info.Codec} {info.Width}×{info.Height}{where} from the guest agent ({info.EncoderKind} encoder, {info.BitrateKbps / 1000.0:0.#} Mbit/s, {info.MaxFps} fps max)");
    }

    /// <summary>Stops the stream and returns the picture to RFB with a full lossless update.</summary>
    public async Task StopAsync()
    {
        if (!IsStreaming) return;
        Mode = null;
        _idleTimer.Stop();
        try { await _agent.StopStreamAsync().ConfigureAwait(true); }
        catch (Exception ex) { Message?.Invoke("Stop stream: " + ex.Message); }
        BackToRfb();
    }

    private void BackToRfb()
    {
        IdleFallbackActive = false;
        _view.ShowFramebuffer();
        _client.RefreshPicture();
        _client.UpdateInterval = TimeSpan.Zero;
    }

    private void OnAgentStopped(string? reason)
    {
        if (!IsStreaming) return;
        Mode = null;
        _idleTimer.Stop();
        BackToRfb();
        Message?.Invoke($"Stream ended: {reason ?? "stopped by the agent"}; back to VNC");
        Ended?.Invoke(reason);
    }

    private void OnFrame(AgentFrame frame)
    {
        if (!IsStreaming) return;
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, frame.Payload.Length);
        _frames.Writer.TryWrite(frame);
    }

    private byte[] Rent(int size)
    {
        while (_pool.TryTake(out var buffer))
            if (buffer.Length == size) return buffer;
        return new byte[size];
    }

    private void Return(byte[] buffer)
    {
        if (_pool.Count < 4) _pool.Add(buffer);
    }

    /// <summary>Asks the agent for a key frame (at most twice a second) after frames were lost on the way or dropped here.</summary>
    private void RequestKeyframe()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastKeyframeRequest) < 500) return;
        Interlocked.Exchange(ref _lastKeyframeRequest, now);
        _ = _dispatcher.BeginInvoke(async () =>
        {
            try { await _agent.SetStreamAsync(null, null, keyframe: true); }
            catch (Exception ex) { Message?.Invoke("Key frame request failed: " + ex.Message); }
        });
    }

    /// <summary>Puts a line in the status bar and the trace log (the decoder and presentation path in use, fallbacks).</summary>
    private void Report(string line)
    {
        System.Diagnostics.Trace.WriteLine("BetterVNC stream: " + line);
        _ = _dispatcher.BeginInvoke(() => Message?.Invoke(line));
    }

    /// <summary>Says which decoder runs and how its pictures reach the screen, from the first picture it delivered.</summary>
    private void DescribeDecoder(H264Decoder decoder, bool texture)
    {
        bool gpu = _view.GpuVideoDevice != null;
        string decode = texture ? "hardware (DXVA)"
            : decoder.HardwareFailure is { } why ? $"software (hardware refused: {why})"
            : "software";
        DecodePath = (texture ? "DXVA" : "software") + (gpu ? " → GPU" : " → WPF");
        Report($"H.264 decoder: {decoder.Name}, {decode}; {_view.GpuVideoStatus}");
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        H264Decoder? h264 = null;
        int generation = -1;
        int? lastSequence = null;
        bool awaitingKeyframe = false;
        bool softwareOnly = false;     // the GPU path failed on this stream: decode on the CPU from then on
        bool reportDecoder = false;    // describe the decode path once the first picture shows what it really is
        bool reportedJpeg = false;
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!IsStreaming) continue;
                var h = frame.Header;
                var info = _agent.StreamInfo;
                string codec = h.Codec ?? info?.Codec ?? AgentVideoCodec.Jpeg;
                // Where the frame sits in the desktop when it shows only part of it (the frame header wins over videoStarted).
                (int X, int Y)? offset = h.OffsetX is { } hx ? (hx, h.OffsetY ?? 0) : info?.OffsetX is { } ix ? (ix, info.OffsetY ?? 0) : null;
                int gen = Volatile.Read(ref _generation);
                if (gen != generation)
                {
                    // A new stream: decoder state from the previous one is meaningless.
                    generation = gen;
                    lastSequence = null;
                    awaitingKeyframe = false;
                    softwareOnly = false;
                    reportedJpeg = false;
                    _lostFrames = false;
                    h264?.Dispose();
                    h264 = null;
                }

                try
                {
                    if (string.Equals(codec, AgentVideoCodec.H264, StringComparison.OrdinalIgnoreCase))
                    {
                        if (h264 == null)
                        {
                            // With the view's device the decoder uses DXVA; it falls back to software by itself if refused.
                            h264 = new H264Decoder(softwareOnly ? null : _view.GpuVideoDevice);
                            reportDecoder = true;
                        }
                        // Delta frames need every frame before them since the last key frame. A gap in the sequence
                        // (the agent dropped frames on a slow link) or a drop here means: wait for the next key frame.
                        if (_lostFrames) { _lostFrames = false; awaitingKeyframe = true; }
                        if (h.Sequence is { } seq)
                        {
                            if (lastSequence is { } last && seq != last + 1) awaitingKeyframe = true;
                            lastSequence = seq;
                        }
                        if (awaitingKeyframe)
                        {
                            if (h.Keyframe != true)
                            {
                                Interlocked.Increment(ref _framesSkipped);
                                RequestKeyframe();
                                continue;
                            }
                            awaitingKeyframe = false;
                        }
                        bool show = _frames.Reader.Count == 0; // latest picture wins; older ones are decoded for their references only
                        bool shown = false, gpuRefused = false;
                        var decoder = h264;
                        h264.Decode(frame.Payload.Span, h.Timestamp ?? 0, (in DecodedFrame picture) =>
                        {
                            if (reportDecoder)
                            {
                                reportDecoder = false;
                                DescribeDecoder(decoder, picture.IsTexture);
                            }
                            if (!show) return;
                            // GPU: the texture (or the NV12 planes) goes to the view's swap chain from this thread, no queue.
                            if (_view.PresentVideoFrame(in picture, offset)) { shown = true; return; }
                            if (picture.IsTexture) { gpuRefused = true; return; }
                            var bgrx = Rent(picture.Width * picture.Height * 4);
                            picture.ToBgrx(bgrx);
                            _view.PresentExternalFrame(bgrx, picture.Width, picture.Height, Return, offset);
                            shown = true;
                        });
                        if (gpuRefused)
                        {
                            // The GPU surface gave up on a hardware picture: decode in software from the next key frame.
                            h264.Dispose();
                            h264 = null;
                            softwareOnly = true;
                            awaitingKeyframe = true;
                            RequestKeyframe();
                            Report($"{_view.GpuVideoStatus}; H.264 now decodes in software");
                        }
                        if (!shown) { Interlocked.Increment(ref _framesSkipped); continue; }
                    }
                    else
                    {
                        if (_frames.Reader.Count > 0) { Interlocked.Increment(ref _framesSkipped); continue; } // every JPEG stands alone: skip to the newest
                        int w = h.Width ?? info?.Width ?? 0;
                        int hgt = h.Height ?? info?.Height ?? 0;
                        var bgrx = _jpeg.DecodeToBgrx(frame.Payload.Span, w, hgt);
                        bool gpu = _view.PresentVideoFrame(bgrx, w, hgt, offset);
                        if (!gpu) _view.PresentExternalFrame(bgrx, w, hgt, null, offset);
                        if (!reportedJpeg)
                        {
                            reportedJpeg = true;
                            DecodePath = gpu ? "JPEG → GPU" : "JPEG → WPF";
                            Report($"Motion JPEG decoder: WIC; {_view.GpuVideoStatus}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _framesSkipped);
                    _ = _dispatcher.BeginInvoke(() => Message?.Invoke("Frame decode failed: " + ex.Message));
                    if (h264 != null)
                    {
                        if (h264.IsHardware)
                        {
                            // Never fight a failing GPU decoder: the CPU path decodes everything the agent sends.
                            h264.Dispose();
                            h264 = null;
                            softwareOnly = true;
                            Report($"Hardware H.264 decode failed ({ex.Message}); decoding in software");
                        }
                        awaitingKeyframe = true;
                        RequestKeyframe();
                    }
                    continue;
                }
                Interlocked.Increment(ref _framesDecoded);
                _ = _dispatcher.BeginInvoke(OnFramePresented, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally { h264?.Dispose(); }
    }

    private void OnFramePresented()
    {
        if (!IsStreaming) return;
        if (IdleFallbackActive)
        {
            // Movement again: the stream is on screen and RFB goes back to its trickle.
            IdleFallbackActive = false;
            _client.UpdateInterval = RfbIntervalWhileStreaming;
        }
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void OnIdle()
    {
        _idleTimer.Stop();
        if (!IsStreaming || IdleFallbackActive) return;
        // Nothing moved for a while: show the exact pixels from RFB until the next frame arrives.
        IdleFallbackActive = true;
        _view.ShowFramebuffer();
        _client.RefreshPicture();
        _client.UpdateInterval = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _agent.StreamFrame -= OnFrame;
        _agent.StreamStopped -= OnAgentStopped;
        _idleTimer.Stop();
        _cts.Cancel();
        _frames.Writer.TryComplete();
        if (IsStreaming)
        {
            Mode = null;
            _ = _agent.StopStreamAsync();
            if (_client.IsConnected) BackToRfb();
        }
        _cts.Dispose();
    }
}
