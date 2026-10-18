using System.Collections.Concurrent;
using System.Net.Sockets;

namespace BetterVnc.Agent.Protocol;

public sealed class AgentConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = AgentServer.DefaultPort;
    /// <summary>Shared token or password configured in the agent. Null when the viewer authenticates with a paired key.</summary>
    public string? Token { get; init; }
    /// <summary>Key obtained from <see cref="AgentConnection.PairAsync"/> earlier; takes precedence over <see cref="Token"/>.</summary>
    public string? PairKey { get; init; }
    /// <summary>Stable id of this viewer installation; required for pairing and for paired-key authentication.</summary>
    public string? ViewerId { get; init; }
    /// <summary>Name shown in the agent's approval dialog, usually the machine name.</summary>
    public string? ViewerName { get; init; }
    /// <summary>How long to wait for the person at the guest screen to click Allow.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public string AppName { get; init; } = "BetterVNC";
    public string OsName { get; init; } = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    public long MaxPayloadBytes { get; init; } = AgentFraming.DefaultMaxPayloadBytes;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>The viewer's end of an agent connection: handshake, request/reply correlation and change notifications.</summary>
public sealed class AgentConnection : IDisposable
{
    private readonly AgentConnectionOptions _options;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentFrame>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _nextId;
    private int _disposed;
    private volatile bool _closed;

    public AgentConnection(AgentConnectionOptions options)
    {
        _options = options;
    }

    public string? RemoteOs { get; private set; }
    public string? RemoteApp { get; private set; }
    public int RemoteVersion { get; private set; }
    /// <summary>Formats on the guest clipboard at connect time.</summary>
    public string[] RemoteFormats { get; private set; } = Array.Empty<string>();
    /// <summary>Capabilities the agent announced; see <see cref="AgentFeature"/>.</summary>
    public string[] RemoteFeatures { get; private set; } = Array.Empty<string>();
    /// <summary>True when the agent accepts <see cref="ScrollAsync"/>.</summary>
    public bool SupportsScroll => RemoteFeatures.Contains(AgentFeature.Scroll);
    /// <summary>True when the agent can stream its screen (<see cref="StartVideoAsync"/>); <see cref="RemoteCodecs"/> says in what.</summary>
    public bool SupportsVideo => RemoteFeatures.Contains(AgentFeature.Video) && RemoteCodecs.Length > 0;
    /// <summary>Codecs the agent can encode, most preferred first; empty when it cannot stream.</summary>
    public string[] RemoteCodecs { get; private set; } = Array.Empty<string>();
    public bool IsConnected => _stream != null && _disposed == 0 && !_closed;

    /// <summary>The guest clipboard changed; the message lists formats and, for files, names and sizes.</summary>
    public event Action<AgentMessage>? ClipboardChanged;
    /// <summary>One encoded video frame (header plus payload), raised on the receive thread while a stream runs.</summary>
    public event Action<AgentFrame>? VideoFrameReceived;
    /// <summary>The agent ended the stream by itself; the string says why (may be null).</summary>
    public event Action<string?>? VideoStopped;
    /// <summary>The connection ended; the exception is null for a clean close by the agent.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>The key derived by <see cref="PairAsync"/>; the caller stores it and passes it as <see cref="AgentConnectionOptions.PairKey"/> next time.</summary>
    public string? PairedKey { get; private set; }

    /// <summary>Connects and authenticates with the paired key or the token from the options.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        var secret = _options.PairKey ?? _options.Token;
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("No paired key and no token; call PairAsync first.");
        var (tcp, stream) = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(_options.ConnectTimeout);
            var hello = await ReadHelloAsync(stream, hsCts.Token).ConfigureAwait(false);
            await HandshakeAsync(stream, hello, secret, hsCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        Attach(tcp, stream);
    }

    /// <summary>
    /// Connects, asks the agent to pair (the person at the guest screen must click Allow), derives the shared key into
    /// <see cref="PairedKey"/> and finishes the handshake with it, so the connection is usable afterwards.
    /// </summary>
    public async Task PairAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ViewerId)) throw new InvalidOperationException("Pairing needs a ViewerId.");
        var (tcp, stream) = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(_options.ConnectTimeout);
            await ReadHelloAsync(stream, hsCts.Token).ConfigureAwait(false);

            using var mine = AgentPairing.NewKeyPair();
            await AgentFraming.WriteAsync(stream, new AgentMessage
            {
                Type = AgentMessageType.Pair,
                Version = AgentMessage.ProtocolVersion,
                Os = _options.OsName,
                App = _options.AppName,
                ViewerId = _options.ViewerId,
                ViewerName = _options.ViewerName ?? Environment.MachineName,
                PublicKey = AgentPairing.ExportPublicKey(mine),
            }, default, hsCts.Token).ConfigureAwait(false);

            using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pairCts.CancelAfter(_options.PairingTimeout);
            AgentFrame? paired;
            try { paired = await AgentFraming.ReadAsync(stream, 64 * 1024, pairCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AgentPairingException(AgentErrorCode.PairingDenied, "Nobody approved the pairing in the remote screen in time.");
            }
            if (paired == null) throw new AgentPairingException(AgentErrorCode.PairingDenied, "The agent closed the connection during pairing.");
            if (paired.Header.Type == AgentMessageType.Error)
                throw new AgentPairingException(paired.Header.Code ?? AgentErrorCode.PairingDenied, paired.Header.Message ?? "Pairing failed.");
            if (paired.Header.Type != AgentMessageType.Paired || string.IsNullOrEmpty(paired.Header.PublicKey))
                throw new AgentProtocolException($"Unexpected message '{paired.Header.Type}' during pairing.");
            var key = AgentPairing.DeriveKey(mine, paired.Header.PublicKey);

            // The agent restarts the handshake with a fresh nonce; authenticate with the new key.
            using var hs2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs2.CancelAfter(_options.ConnectTimeout);
            var hello = await ReadHelloAsync(stream, hs2.Token).ConfigureAwait(false);
            await HandshakeAsync(stream, hello, key, hs2.Token).ConfigureAwait(false);
            PairedKey = key;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        Attach(tcp, stream);
    }

    private async Task<(TcpClient, NetworkStream)> OpenAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(_options.ConnectTimeout);
            try { await tcp.ConnectAsync(_options.Host, _options.Port, connectCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"No answer from the agent at {_options.Host}:{_options.Port}.");
            }
            catch { tcp.Dispose(); throw; }
        }
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        return (tcp, tcp.GetStream());
    }

    private async Task<AgentFrame> ReadHelloAsync(NetworkStream stream, CancellationToken ct)
    {
        var hello = await AgentFraming.ReadAsync(stream, 64 * 1024, ct).ConfigureAwait(false)
                    ?? throw new AgentProtocolException("The agent closed the connection before saying hello.");
        if (hello.Header.Type == AgentMessageType.Error) throw new AgentProtocolException(hello.Header.Message ?? "Agent error.");
        if (hello.Header.Type != AgentMessageType.Hello || string.IsNullOrEmpty(hello.Header.Nonce))
            throw new AgentProtocolException($"Unexpected first message '{hello.Header.Type}'; is this really a BetterVNC agent?");
        if (hello.Header.Version is not { } v || v < 1)
            throw new AgentProtocolException("The agent did not announce a protocol version.");
        RemoteVersion = v;
        RemoteOs = hello.Header.Os;
        RemoteApp = hello.Header.App;
        return hello;
    }

    private async Task HandshakeAsync(NetworkStream stream, AgentFrame hello, string secret, CancellationToken ct)
    {
        var myNonce = AgentAuth.NewNonce();
        await AgentFraming.WriteAsync(stream, new AgentMessage
        {
            Type = AgentMessageType.Hello,
            Version = AgentMessage.ProtocolVersion,
            Os = _options.OsName,
            App = _options.AppName,
            Nonce = myNonce,
            Proof = AgentAuth.Prove(secret, hello.Header.Nonce!),
            ViewerId = _options.ViewerId,
            ViewerName = _options.ViewerName,
        }, default, ct).ConfigureAwait(false);

        var welcome = await AgentFraming.ReadAsync(stream, 64 * 1024, ct).ConfigureAwait(false)
                      ?? throw new AgentAuthenticationException("The agent closed the connection during authentication.");
        if (welcome.Header.Type == AgentMessageType.Error)
        {
            if (welcome.Header.Code == AgentErrorCode.NotPaired)
                throw new AgentPairingException(AgentErrorCode.NotPaired, welcome.Header.Message ?? "This viewer is not paired with the agent.");
            throw new AgentAuthenticationException(welcome.Header.Message ?? "Authentication failed.");
        }
        if (welcome.Header.Type != AgentMessageType.Welcome)
            throw new AgentProtocolException($"Unexpected message '{welcome.Header.Type}' during handshake.");
        if (!AgentAuth.Verify(secret, myNonce, welcome.Header.Proof))
            throw new AgentAuthenticationException("The agent could not prove it knows the token.");
        RemoteFormats = welcome.Header.Formats ?? Array.Empty<string>();
        RemoteFeatures = welcome.Header.Features ?? Array.Empty<string>();
        RemoteCodecs = welcome.Header.Codecs ?? Array.Empty<string>();
    }

    private void Attach(TcpClient tcp, NetworkStream stream)
    {
        _tcp = tcp;
        _stream = stream;
        _ = Task.Run(() => ReceiveLoopAsync(stream, _cts.Token));
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await AgentFraming.ReadAsync(stream, _options.MaxPayloadBytes, ct).ConfigureAwait(false);
                if (frame == null) break;
                var h = frame.Header;
                if (h.Id is { } id && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(frame);
                    continue;
                }
                switch (h.Type)
                {
                    case AgentMessageType.ClipboardChanged:
                        ClipboardChanged?.Invoke(h);
                        break;
                    case AgentMessageType.VideoFrame:
                        VideoFrameReceived?.Invoke(frame);
                        break;
                    case AgentMessageType.VideoStopped:
                        VideoStopped?.Invoke(h.Message);
                        break;
                    case AgentMessageType.Ping:
                        await SendAsync(AgentMessage.Of(AgentMessageType.Pong, h.Id), default, ct).ConfigureAwait(false);
                        break;
                    case AgentMessageType.Error when h.Id == null:
                        throw new AgentProtocolException(h.Message ?? "Agent error.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException ex) when (_disposed != 0 || ct.IsCancellationRequested) { _ = ex; }
        catch (Exception ex) { error = ex; }

        _closed = true;
        foreach (var kv in _pending) kv.Value.TrySetException(error ?? new IOException("The agent connection was closed."));
        _pending.Clear();
        if (_disposed == 0) Disconnected?.Invoke(error);
    }

    private async Task SendAsync(AgentMessage header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await AgentFraming.WriteAsync(stream, header, payload, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task<AgentFrame> RequestAsync(AgentMessage header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        header.Id = id;
        var tcs = new TaskCompletionSource<AgentFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(header, payload, ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            var reply = await tcs.Task.ConfigureAwait(false);
            if (reply.Header.Type == AgentMessageType.Error) throw new AgentProtocolException(reply.Header.Message ?? "The agent reported an error.");
            return reply;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Fetches the guest clipboard in one format; null when that format is (no longer) available.</summary>
    public async Task<AgentFrame?> GetClipboardAsync(string format, CancellationToken ct)
    {
        var reply = await RequestAsync(new AgentMessage { Type = AgentMessageType.GetClipboard, Format = format }, default, ct).ConfigureAwait(false);
        if (reply.Header.Type != AgentMessageType.ClipboardData) throw new AgentProtocolException($"Unexpected reply '{reply.Header.Type}'.");
        return reply.Header.Empty == true ? null : reply;
    }

    /// <summary>Replaces the guest clipboard with text, HTML or a PNG.</summary>
    public Task SetClipboardAsync(string format, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        RequestAsync(new AgentMessage { Type = AgentMessageType.SetClipboard, Format = format }, data, ct);

    /// <summary>Copies local files to the guest and puts them on its clipboard.</summary>
    public async Task SetClipboardFilesAsync(IEnumerable<string> localPaths, CancellationToken ct)
    {
        var (entries, payload) = FileBundle.Pack(localPaths, _options.MaxPayloadBytes);
        await RequestAsync(new AgentMessage { Type = AgentMessageType.SetClipboard, Format = AgentFormat.Files, Files = entries }, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Injects a high-resolution scroll in the guest (no reply). <paramref name="dx"/>/<paramref name="dy"/> are pixels,
    /// positive = right / up; <paramref name="wheelDx"/>/<paramref name="wheelDy"/> the same movement in Windows wheel
    /// units when known. Throws <see cref="NotSupportedException"/> when the agent did not announce the feature.
    /// </summary>
    public Task ScrollAsync(double dx, double dy, int? x, int? y, string? phase, double? wheelDx, double? wheelDy, CancellationToken ct)
    {
        if (!SupportsScroll) throw new NotSupportedException("The agent does not support scroll injection.");
        return SendAsync(new AgentMessage
        {
            Type = AgentMessageType.Scroll,
            Dx = dx, Dy = dy, X = x, Y = y, Phase = phase, WheelDx = wheelDx, WheelDy = wheelDy,
        }, default, ct);
    }

    /// <summary>
    /// The negotiated parameters of a stream, from the agent's <see cref="AgentMessageType.VideoStarted"/> reply.
    /// <paramref name="OffsetX"/>/<paramref name="OffsetY"/> say where the frames sit in the desktop when they show only
    /// part of it (see <see cref="AgentMessage.OffsetX"/>); null when they show the whole desktop.
    /// </summary>
    public sealed record VideoStartInfo(string Codec, int BitrateKbps, int MaxFps, int Width, int Height, string EncoderKind, int? OffsetX = null, int? OffsetY = null);

    /// <summary>
    /// Asks the agent to stream its screen. <paramref name="codecs"/> lists what this side can decode, most preferred
    /// first; the agent picks the first it can encode. Frames arrive through <see cref="VideoFrameReceived"/>. Throws
    /// <see cref="NotSupportedException"/> when the agent did not announce video, <see cref="AgentProtocolException"/>
    /// when it refuses (no common codec, capture failed).
    /// </summary>
    public Task<VideoStartInfo> StartVideoAsync(IEnumerable<string> codecs, int bitrateKbps, int maxFps, CancellationToken ct) =>
        StartVideoAsync(codecs, bitrateKbps, maxFps, null, ct);

    /// <summary>
    /// <see cref="StartVideoAsync(IEnumerable{string}, int, int, CancellationToken)"/> with the size of the area this side
    /// shows the picture in, in device pixels (see <see cref="AgentMessage.Width"/>): an agent that can scale encodes no
    /// larger than that. Null, or 0 for a side, means no limit.
    /// </summary>
    public async Task<VideoStartInfo> StartVideoAsync(IEnumerable<string> codecs, int bitrateKbps, int maxFps, (int Width, int Height)? area, CancellationToken ct)
    {
        if (!SupportsVideo) throw new NotSupportedException("The agent does not stream video.");
        var reply = await RequestAsync(new AgentMessage
        {
            Type = AgentMessageType.StartVideo,
            Codecs = codecs.ToArray(),
            Bitrate = bitrateKbps,
            MaxFps = maxFps,
            Width = area?.Width,
            Height = area?.Height,
        }, default, ct).ConfigureAwait(false);
        var h = reply.Header;
        if (h.Type != AgentMessageType.VideoStarted || string.IsNullOrEmpty(h.Codec))
            throw new AgentProtocolException($"Unexpected reply '{h.Type}' to startVideo.");
        return new VideoStartInfo(h.Codec, h.Bitrate ?? bitrateKbps, h.MaxFps ?? maxFps, h.Width ?? 0, h.Height ?? 0, h.Encoder ?? AgentVideoEncoder.Software, h.OffsetX, h.OffsetY);
    }

    /// <summary>Changes the running stream's bitrate and/or frame-rate cap (null keeps a value), optionally asking for a key frame.</summary>
    public Task SetVideoAsync(int? bitrateKbps, int? maxFps, bool keyframe, CancellationToken ct) =>
        RequestAsync(new AgentMessage { Type = AgentMessageType.SetVideo, Bitrate = bitrateKbps, MaxFps = maxFps, KeyframeRequest = keyframe ? true : null }, default, ct);

    /// <summary>
    /// Tells the agent the area this side shows the picture in changed (window resized, fit and 1:1 switched), in device
    /// pixels; 0 for a side means no limit. Agents that cannot scale, or predate this, accept it and change nothing.
    /// </summary>
    public Task SetVideoAreaAsync(int width, int height, CancellationToken ct) =>
        RequestAsync(new AgentMessage { Type = AgentMessageType.SetVideo, Width = width, Height = height }, default, ct);

    /// <summary>Stops the stream; frames already in flight may still arrive briefly.</summary>
    public Task StopVideoAsync(CancellationToken ct) =>
        RequestAsync(AgentMessage.Of(AgentMessageType.StopVideo), default, ct);

    public async Task<TimeSpan> PingAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RequestAsync(AgentMessage.Of(AgentMessageType.Ping), default, ct).ConfigureAwait(false);
        return sw.Elapsed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { _tcp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
