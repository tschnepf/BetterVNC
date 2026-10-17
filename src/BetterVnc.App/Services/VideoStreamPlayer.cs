using System.Threading.Channels;
using System.Windows.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.App.Controls;
using BetterVnc.App.Models;
using BetterVnc.Protocol;

namespace BetterVnc.App.Services;

/// <summary>
/// Plays the guest agent's video stream into a <see cref="VncView"/> while the RFB connection goes on carrying input,
/// cursor shapes and resizes at a trickle. Frames are decoded off the UI thread with a latest-frame-wins policy, so a
/// slow decoder skips frames instead of falling behind. When the agent sends nothing for a moment (the screen is still)
/// the view switches back to the RFB framebuffer and a full lossless update is requested, so a quiet desktop is always
/// shown pixel-exact; the first new frame switches back to the stream.
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
    private readonly Channel<AgentFrame> _frames = Channel.CreateBounded<AgentFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _idleTimer;
    private readonly WicJpegDecoder _jpeg = new();
    private Task? _decodeLoop;
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
        var info = await _agent.StartStreamAsync(mode.BitrateKbps, mode.MaxFps).ConfigureAwait(true);
        Mode = mode;
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
        Message?.Invoke($"Streaming {info.Codec} {info.Width}×{info.Height} from the guest agent ({info.EncoderKind} encoder, {info.BitrateKbps / 1000.0:0.#} Mbit/s, {info.MaxFps} fps max)");
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
        if (!_frames.Writer.TryWrite(frame)) Interlocked.Increment(ref _framesSkipped);
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!IsStreaming) continue;
                var h = frame.Header;
                int w = h.Width ?? _agent.StreamInfo?.Width ?? 0;
                int hgt = h.Height ?? _agent.StreamInfo?.Height ?? 0;
                byte[] bgrx;
                try
                {
                    // Only JPEG is decodable in this build; the codec was negotiated from DecodableCodecs so nothing else arrives.
                    bgrx = _jpeg.DecodeToBgrx(frame.Payload.Span, w, hgt);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _framesSkipped);
                    _ = _dispatcher.BeginInvoke(() => Message?.Invoke("Frame decode failed: " + ex.Message));
                    continue;
                }
                Interlocked.Increment(ref _framesDecoded);
                _view.PresentExternalFrame(bgrx, w, hgt);
                _ = _dispatcher.BeginInvoke(OnFramePresented, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
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
