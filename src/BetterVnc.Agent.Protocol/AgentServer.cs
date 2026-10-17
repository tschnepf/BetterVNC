using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BetterVnc.Agent.Protocol;

public sealed class AgentServerOptions
{
    /// <summary>Shared token or password; null disables token authentication (paired viewers only).</summary>
    public string? Token { get; init; }
    /// <summary>Looks up a paired viewer by id; null disables paired-key authentication.</summary>
    public Func<string, PairedViewer?>? FindViewer { get; init; }
    /// <summary>Called when a pairing was approved so the viewer can be stored.</summary>
    public Action<PairedViewer>? ViewerPaired { get; init; }
    /// <summary>Shows the approval dialog; null (or <see cref="AllowPairing"/> false) refuses new pairings.</summary>
    public IPairingApprover? PairingApprover { get; init; }
    public bool AllowPairing { get; init; } = true;
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    /// <summary>0 picks a free port; read <see cref="AgentServer.Port"/> after <see cref="AgentServer.Start"/>.</summary>
    public int Port { get; init; } = AgentServer.DefaultPort;
    public long MaxPayloadBytes { get; init; } = AgentFraming.DefaultMaxPayloadBytes;
    /// <summary>Where files pushed by a viewer are written before they are put on the clipboard.</summary>
    public string StagingDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "bettervnc-agent");
    public string AppName { get; init; } = "BetterVNC agent";
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The agent's network side: accepts viewers, authenticates them with the shared token, serves clipboard requests
/// against an <see cref="IGuestClipboard"/> and pushes <c>clipboardChanged</c> notifications when the guest clipboard
/// changes. Independent of the OS so it can be exercised in tests with a fake clipboard.
/// </summary>
public sealed class AgentServer : IAsyncDisposable
{
    public const int DefaultPort = 5970;

    private readonly IGuestClipboard _clipboard;
    private readonly IGuestInput? _input;
    private readonly IGuestVideoSource? _video;
    private readonly AgentServerOptions _options;
    private readonly SemaphoreSlim _clipboardLock = new(1, 1);
    private readonly List<Session> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _watchLoop;
    private string? _lastChangeToken;
    private volatile bool _suppressNextChange;

    public AgentServer(IGuestClipboard clipboard, AgentServerOptions options, IGuestInput? input = null, IGuestVideoSource? video = null)
    {
        _clipboard = clipboard;
        _input = input;
        _video = video is { Codecs.Count: > 0 } ? video : null;
        _options = options;
        if (string.IsNullOrWhiteSpace(options.Token) && options.FindViewer == null && (options.PairingApprover == null || !options.AllowPairing))
            throw new ArgumentException("Nothing could ever authenticate: set a token, a viewer lookup, or allow pairing.", nameof(options));
    }

    public int Port { get; private set; }
    public int SessionCount { get { lock (_sessions) return _sessions.Count; } }

    public event Action<string>? Log;
    /// <summary>A viewer authenticated: (viewer id, viewer name, peer address). Ids and names are null for token logins without identity.</summary>
    public event Action<string?, string?, string>? ViewerConnected;

    /// <summary>Capabilities announced to viewers in the welcome message.</summary>
    public string[] Features
    {
        get
        {
            var list = new List<string>(2);
            if (_input != null) list.Add(AgentFeature.Scroll);
            if (_video != null) list.Add(AgentFeature.Video);
            return list.ToArray();
        }
    }

    /// <summary>Codecs announced in the welcome message; null when this agent cannot stream.</summary>
    public string[]? VideoCodecs => _video?.Codecs.ToArray();

    /// <summary>Number of viewers currently receiving a video stream.</summary>
    public int StreamingSessions { get { lock (_sessions) return _sessions.Count(s => s.IsStreaming); } }

    /// <summary>Binds the listener and starts the accept and clipboard-watch loops.</summary>
    public void Start()
    {
        if (_listener != null) throw new InvalidOperationException("Already started.");
        _listener = new TcpListener(_options.ListenAddress, _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _lastChangeToken = SafeChangeToken();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _watchLoop = Task.Run(() => WatchLoopAsync(_cts.Token));
        Log?.Invoke($"Listening on {_options.ListenAddress}:{Port} ({_clipboard.OsName})");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        Session[] sessions;
        lock (_sessions) sessions = _sessions.ToArray();
        foreach (var s in sessions) s.Close();
        foreach (var t in new[] { _acceptLoop, _watchLoop })
        {
            if (t == null) continue;
            try { await t.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    // ------------------------------------------------------------------ accept

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { Log?.Invoke("Accept failed: " + ex.Message); continue; }
            var session = new Session(this, tcp);
            lock (_sessions) _sessions.Add(session);
            _ = session.RunAsync(ct).ContinueWith(_ => { lock (_sessions) _sessions.Remove(session); }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ clipboard watch

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_clipboard.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            string? token = SafeChangeToken();
            if (SessionCount == 0) { _lastChangeToken = token; continue; } // nobody to tell; only changes after a viewer connects are announced
            if (token == _lastChangeToken) continue;
            _lastChangeToken = token;
            if (_suppressNextChange) { _suppressNextChange = false; continue; } // our own SetClipboard
            if (token == null) continue;
            AgentMessage notice;
            try { notice = await DescribeClipboardAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Log?.Invoke("Could not read clipboard: " + ex.Message); continue; }
            if (notice.Formats!.Length == 0) continue;
            Log?.Invoke($"Clipboard changed: {string.Join(", ", notice.Formats)}");
            await BroadcastAsync(notice, ct).ConfigureAwait(false);
        }
    }

    private string? SafeChangeToken()
    {
        try { return _clipboard.GetChangeToken(); }
        catch (Exception ex) { Log?.Invoke("Clipboard poll failed: " + ex.Message); return null; }
    }

    private async Task<AgentMessage> DescribeClipboardAsync(CancellationToken ct)
    {
        await _clipboardLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var formats = _clipboard.GetFormats().ToArray();
                var msg = new AgentMessage { Type = AgentMessageType.ClipboardChanged, Formats = formats };
                if (formats.Contains(AgentFormat.Files))
                {
                    var paths = _clipboard.GetFilePaths();
                    if (paths != null) msg.Files = DescribeFiles(paths);
                }
                return msg;
            }, ct).ConfigureAwait(false);
        }
        finally { _clipboardLock.Release(); }
    }

    private static List<AgentFileEntry> DescribeFiles(IReadOnlyList<string> paths)
    {
        var list = new List<AgentFileEntry>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    long size = 0;
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)) size += new FileInfo(f).Length;
                    list.Add(new AgentFileEntry { Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(p)) + "/", Size = size });
                }
                else if (File.Exists(p))
                {
                    list.Add(new AgentFileEntry { Name = Path.GetFileName(p), Size = new FileInfo(p).Length });
                }
            }
            catch { }
        }
        return list;
    }

    private async Task BroadcastAsync(AgentMessage msg, CancellationToken ct)
    {
        Session[] sessions;
        lock (_sessions) sessions = _sessions.ToArray();
        foreach (var s in sessions)
        {
            if (!s.Authenticated) continue;
            try { await s.SendAsync(msg, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false); }
            catch (Exception ex) { Log?.Invoke("Notify failed: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ requests

    private async Task<AgentFrame> HandleGetClipboardAsync(AgentMessage req, CancellationToken ct)
    {
        var format = req.Format ?? "";
        if (!AgentFormat.All.Contains(format)) return new AgentFrame(AgentMessage.ErrorReply(req.Id, $"Unknown format '{format}'."));
        await _clipboardLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var reply = new AgentMessage { Type = AgentMessageType.ClipboardData, Id = req.Id, Format = format };
                if (format == AgentFormat.Files)
                {
                    var paths = _clipboard.GetFilePaths();
                    if (paths == null || paths.Count == 0) { reply.Empty = true; return new AgentFrame(reply); }
                    var (entries, payload) = FileBundle.Pack(paths, _options.MaxPayloadBytes);
                    reply.Files = entries;
                    return new AgentFrame(reply, payload);
                }
                var data = _clipboard.GetData(format);
                if (data == null) { reply.Empty = true; return new AgentFrame(reply); }
                if (data.LongLength > _options.MaxPayloadBytes) return new AgentFrame(AgentMessage.ErrorReply(req.Id, "Clipboard content exceeds the transfer limit."));
                return new AgentFrame(reply, data);
            }, ct).ConfigureAwait(false);
        }
        finally { _clipboardLock.Release(); }
    }

    private async Task<AgentFrame> HandleSetClipboardAsync(AgentFrame frame, CancellationToken ct)
    {
        var req = frame.Header;
        var format = req.Format ?? "";
        if (!AgentFormat.All.Contains(format)) return new AgentFrame(AgentMessage.ErrorReply(req.Id, $"Unknown format '{format}'."));
        await _clipboardLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (format == AgentFormat.Files)
                {
                    var dir = Path.Combine(_options.StagingDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                    var paths = FileBundle.Unpack(req.Files ?? new List<AgentFileEntry>(), frame.Payload, dir);
                    _suppressNextChange = true;
                    _clipboard.SetFiles(paths);
                    Log?.Invoke($"Clipboard set: {paths.Count} item(s) in {dir}");
                }
                else
                {
                    _suppressNextChange = true;
                    _clipboard.SetData(format, frame.Payload.ToArray());
                    Log?.Invoke($"Clipboard set: {format}, {frame.Payload.Length} bytes");
                }
                // Record the token the change produced so the watcher does not announce our own write.
                _lastChangeToken = SafeChangeToken();
                _suppressNextChange = false;
            }, ct).ConfigureAwait(false);
            return new AgentFrame(AgentMessage.Of(AgentMessageType.Ok, req.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _suppressNextChange = false;
            return new AgentFrame(AgentMessage.ErrorReply(req.Id, ex.Message));
        }
        finally { _clipboardLock.Release(); }
    }

    private void HandleScroll(AgentMessage req)
    {
        if (_input == null) return; // not announced, so a well-behaved viewer never sends it; ignore rather than fail the session
        double dx = req.Dx ?? 0, dy = req.Dy ?? 0;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        Log?.Invoke($"Scroll: dx={dx:0.##} dy={dy:0.##} {req.Phase}");
        try { _input.Scroll(dx, dy, req.WheelDx, req.WheelDy, req.Phase); }
        catch (Exception ex) { Log?.Invoke("Scroll failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ session

    private sealed class Session
    {
        private readonly AgentServer _server;
        private readonly TcpClient _tcp;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private NetworkStream? _stream;

        public Session(AgentServer server, TcpClient tcp)
        {
            _server = server;
            _tcp = tcp;
        }

        public bool Authenticated { get; private set; }
        public string Peer => _tcp.Client.RemoteEndPoint?.ToString() ?? "?";

        public void Close()
        {
            try { _tcp.Close(); } catch { }
        }

        public async Task SendAsync(AgentMessage header, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected.");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await AgentFraming.WriteAsync(stream, header, payload, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var opts = _server._options;
            try
            {
                _tcp.NoDelay = true;
                _stream = _tcp.GetStream();

                // Handshake. A viewer may answer our hello with "pair" first; after an approved pairing we send a
                // fresh hello and the viewer authenticates with the key both sides just derived.
                AgentFrame hello;
                string secret;
                for (;;)
                {
                    var nonce = AgentAuth.NewNonce();
                    await SendAsync(new AgentMessage
                    {
                        Type = AgentMessageType.Hello,
                        Version = AgentMessage.ProtocolVersion,
                        Os = _server._clipboard.OsName,
                        App = opts.AppName,
                        Nonce = nonce,
                    }, default, ct).ConfigureAwait(false);

                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshakeCts.CancelAfter(opts.HandshakeTimeout);
                    var first = await AgentFraming.ReadAsync(_stream, 64 * 1024, handshakeCts.Token).ConfigureAwait(false);
                    if (first == null) return;

                    if (first.Header.Type == AgentMessageType.Pair)
                    {
                        if (!await PairAsync(first.Header, ct).ConfigureAwait(false)) return;
                        continue; // new hello, viewer proves the derived key
                    }
                    if (first.Header.Type != AgentMessageType.Hello || string.IsNullOrEmpty(first.Header.Nonce))
                    {
                        await SendAsync(AgentMessage.ErrorReply(null, "Expected hello."), default, ct).ConfigureAwait(false);
                        return;
                    }

                    var (ok, method, reason, code) = Authenticate(first.Header, nonce);
                    if (!ok)
                    {
                        _server.Log?.Invoke($"{Peer}: rejected ({reason})");
                        await Task.Delay(500, ct).ConfigureAwait(false); // slow down brute force a little
                        var err = AgentMessage.ErrorReply(null, reason);
                        err.Code = code;
                        await SendAsync(err, default, ct).ConfigureAwait(false);
                        return;
                    }
                    hello = first;
                    secret = method;
                    break;
                }

                Authenticated = true;
                var welcome = new AgentMessage
                {
                    Type = AgentMessageType.Welcome,
                    Version = AgentMessage.ProtocolVersion,
                    Os = _server._clipboard.OsName,
                    App = opts.AppName,
                    Proof = AgentAuth.Prove(secret, hello.Header.Nonce!),
                    Features = _server.Features,
                    Codecs = _server.VideoCodecs,
                };
                try { welcome.Formats = (await _server.DescribeClipboardAsync(ct).ConfigureAwait(false)).Formats; } catch { welcome.Formats = Array.Empty<string>(); }
                await SendAsync(welcome, default, ct).ConfigureAwait(false);
                _server.Log?.Invoke($"{Peer}: connected ({hello.Header.App ?? "unknown viewer"} on {hello.Header.Os ?? "?"}{(hello.Header.ViewerName != null ? $", {hello.Header.ViewerName}" : "")})");
                _server.ViewerConnected?.Invoke(hello.Header.ViewerId, hello.Header.ViewerName, Peer);

                while (!ct.IsCancellationRequested)
                {
                    var frame = await AgentFraming.ReadAsync(_stream, opts.MaxPayloadBytes, ct).ConfigureAwait(false);
                    if (frame == null) break;
                    var reply = await DispatchAsync(frame, ct).ConfigureAwait(false);
                    if (reply != null) await SendAsync(reply.Header, reply.Payload, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (AgentProtocolException ex)
            {
                _server.Log?.Invoke($"{Peer}: protocol error: {ex.Message}");
                try { await SendAsync(AgentMessage.ErrorReply(null, ex.Message), default, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            catch (Exception ex)
            {
                _server.Log?.Invoke($"{Peer}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                StopVideo(notifyViewer: false);
                if (Authenticated) _server.Log?.Invoke($"{Peer}: disconnected");
                Close();
            }
        }

        // ---- video

        private readonly object _videoLock = new();
        private IVideoSession? _videoSession;
        private System.Threading.Channels.Channel<(AgentMessage Header, byte[] Data)>? _frames;
        private Task? _framePump;
        private int _frameSequence;
        private long _framesDropped;

        public bool IsStreaming { get { lock (_videoLock) return _videoSession != null; } }

        private AgentFrame HandleStartVideo(AgentMessage req)
        {
            var source = _server._video;
            if (source == null) return new AgentFrame(AgentMessage.ErrorReply(req.Id, "This agent cannot stream its screen."));

            // The viewer lists what it can decode, most preferred first; the first one this source produces wins.
            var wanted = req.Codecs is { Length: > 0 } ? req.Codecs : req.Codec != null ? new[] { req.Codec } : source.Codecs.ToArray();
            var codec = wanted.FirstOrDefault(c => source.Codecs.Contains(c, StringComparer.OrdinalIgnoreCase));
            if (codec == null)
                return new AgentFrame(AgentMessage.ErrorReply(req.Id, $"No common video codec: the viewer decodes {string.Join(", ", wanted)}, this agent encodes {string.Join(", ", source.Codecs)}."));

            var request = new VideoRequest
            {
                Codec = codec,
                BitrateKbps = Math.Clamp(req.Bitrate ?? 8_000, 250, 200_000),
                MaxFps = Math.Clamp(req.MaxFps ?? 60, 1, 120),
            };

            lock (_videoLock)
            {
                StopVideoLocked();
                IVideoSession? started = null;
                var frames = System.Threading.Channels.Channel.CreateBounded<(AgentMessage Header, byte[] Data)>(new System.Threading.Channels.BoundedChannelOptions(3)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, // a slow link sees fewer frames, never older ones
                    SingleReader = true,
                }, dropped =>
                {
                    Interlocked.Increment(ref _framesDropped);
                    // With a delta codec the frames after a dropped one cannot be decoded; the next one must be a key frame.
                    if (dropped.Header.Keyframe != true) { try { started?.RequestKeyframe(); } catch { } }
                });
                _frames = frames;
                _frameSequence = 0;
                _framesDropped = 0;
                IVideoSession session;
                try
                {
                    session = source.Start(request,
                        frame => QueueFrame(frames, frame),
                        reason => _ = OnVideoEndedAsync(frames, reason));
                }
                catch (Exception ex)
                {
                    _frames = null;
                    _server.Log?.Invoke($"{Peer}: video start failed: {ex.Message}");
                    return new AgentFrame(AgentMessage.ErrorReply(req.Id, "Cannot capture the screen: " + ex.Message));
                }
                _videoSession = session;
                started = session;
                _framePump = Task.Run(() => PumpFramesAsync(frames));
                _server.Log?.Invoke($"{Peer}: video started ({session.Codec}, {session.BitrateKbps} kbit/s, {session.MaxFps} fps max, {session.Width}x{session.Height}{(session.Offset is { } at ? $" at {at.X},{at.Y}" : "")}, {session.EncoderKind})");
                return new AgentFrame(new AgentMessage
                {
                    Type = AgentMessageType.VideoStarted,
                    Id = req.Id,
                    Codec = session.Codec,
                    Bitrate = session.BitrateKbps,
                    MaxFps = session.MaxFps,
                    Width = session.Width,
                    Height = session.Height,
                    OffsetX = session.Offset?.X,
                    OffsetY = session.Offset?.Y,
                    Encoder = session.EncoderKind,
                });
            }
        }

        private void QueueFrame(System.Threading.Channels.Channel<(AgentMessage, byte[])> frames, VideoFrame frame)
        {
            var header = new AgentMessage
            {
                Type = AgentMessageType.VideoFrame,
                Keyframe = frame.Keyframe,
                Timestamp = frame.TimestampMicros,
                Width = frame.Width,
                Height = frame.Height,
                OffsetX = frame.OffsetX,
                OffsetY = frame.OffsetY,
                Sequence = Interlocked.Increment(ref _frameSequence),
            };
            // The source may reuse its buffer as soon as we return, so the bytes are copied here, once.
            frames.Writer.TryWrite((header, frame.Data.ToArray()));
        }

        private async Task PumpFramesAsync(System.Threading.Channels.Channel<(AgentMessage Header, byte[] Data)> frames)
        {
            try
            {
                await foreach (var (header, data) in frames.Reader.ReadAllAsync().ConfigureAwait(false))
                    await SendAsync(header, data, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The session is going away; RunAsync's finally stops the capture.
            }
            catch (Exception ex)
            {
                _server.Log?.Invoke($"{Peer}: video send failed: {ex.Message}");
            }
        }

        private async Task OnVideoEndedAsync(System.Threading.Channels.Channel<(AgentMessage, byte[])> frames, string? reason)
        {
            lock (_videoLock)
            {
                if (_frames != frames) return; // a newer stream replaced this one
                StopVideoLocked();
            }
            _server.Log?.Invoke($"{Peer}: video ended: {reason ?? "capture stopped"}");
            try { await SendAsync(new AgentMessage { Type = AgentMessageType.VideoStopped, Message = reason }, default, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        private AgentFrame HandleSetVideo(AgentMessage req)
        {
            lock (_videoLock)
            {
                var session = _videoSession;
                if (session == null) return new AgentFrame(AgentMessage.ErrorReply(req.Id, "No video stream is running."));
                try
                {
                    if (req.Bitrate != null || req.MaxFps != null)
                        session.Set(req.Bitrate is { } b ? Math.Clamp(b, 250, 200_000) : null, req.MaxFps is { } f ? Math.Clamp(f, 1, 120) : null);
                    if (req.KeyframeRequest == true) session.RequestKeyframe();
                }
                catch (Exception ex)
                {
                    return new AgentFrame(AgentMessage.ErrorReply(req.Id, ex.Message));
                }
                return new AgentFrame(new AgentMessage { Type = AgentMessageType.Ok, Id = req.Id, Bitrate = session.BitrateKbps, MaxFps = session.MaxFps });
            }
        }

        private AgentFrame HandleStopVideo(AgentMessage req)
        {
            StopVideo(notifyViewer: false);
            return new AgentFrame(AgentMessage.Of(AgentMessageType.Ok, req.Id));
        }

        private void StopVideo(bool notifyViewer)
        {
            bool wasRunning;
            lock (_videoLock)
            {
                wasRunning = _videoSession != null;
                StopVideoLocked();
            }
            if (wasRunning)
            {
                _server.Log?.Invoke($"{Peer}: video stopped" + (_framesDropped > 0 ? $" ({_framesDropped} frame(s) dropped on a slow link)" : ""));
                if (notifyViewer)
                    try { SendAsync(new AgentMessage { Type = AgentMessageType.VideoStopped }, default, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            }
        }

        /// <summary>Caller holds <see cref="_videoLock"/>.</summary>
        private void StopVideoLocked()
        {
            var session = _videoSession;
            _videoSession = null;
            try { session?.Dispose(); } catch (Exception ex) { _server.Log?.Invoke($"{Peer}: video dispose failed: {ex.Message}"); }
            _frames?.Writer.TryComplete();
            _frames = null;
            _framePump = null;
        }

        /// <summary>Checks the viewer's proof against its paired key (by viewer id) and then the token. Returns the secret that matched.</summary>
        private (bool ok, string secret, string reason, string code) Authenticate(AgentMessage hello, string nonce)
        {
            var opts = _server._options;
            PairedViewer? viewer = null;
            if (!string.IsNullOrEmpty(hello.ViewerId) && opts.FindViewer != null)
            {
                viewer = opts.FindViewer(hello.ViewerId);
                if (viewer != null && AgentAuth.Verify(viewer.Key, nonce, hello.Proof))
                {
                    viewer.LastSeen = DateTime.Now;
                    return (true, viewer.Key, "", "");
                }
            }
            if (!string.IsNullOrEmpty(opts.Token) && AgentAuth.Verify(opts.Token, nonce, hello.Proof))
                return (true, opts.Token, "", "");

            if (!string.IsNullOrEmpty(hello.ViewerId) && viewer == null && string.IsNullOrEmpty(opts.Token))
                return (false, "", "This viewer is not paired with the agent.", AgentErrorCode.NotPaired);
            if (!string.IsNullOrEmpty(hello.ViewerId) && viewer == null)
                return (false, "", "This viewer is not paired with the agent and the token does not match.", AgentErrorCode.NotPaired);
            return (false, "", viewer != null ? "The paired key does not match; pair again." : "Authentication failed: the token does not match.",
                viewer != null ? AgentErrorCode.NotPaired : AgentErrorCode.AuthFailed);
        }

        /// <summary>Runs the approval dialog and answers <c>paired</c> or an error. Returns false when the session should end.</summary>
        private async Task<bool> PairAsync(AgentMessage req, CancellationToken ct)
        {
            var opts = _server._options;
            if (string.IsNullOrEmpty(req.ViewerId) || string.IsNullOrEmpty(req.PublicKey))
            {
                await SendAsync(AgentMessage.ErrorReply(null, "Pairing needs a viewer id and a public key."), default, ct).ConfigureAwait(false);
                return false;
            }
            if (!opts.AllowPairing || opts.PairingApprover == null || opts.ViewerPaired == null)
            {
                var err = AgentMessage.ErrorReply(null, "This agent does not accept new pairings; use its token instead.");
                err.Code = AgentErrorCode.PairingDisabled;
                await SendAsync(err, default, ct).ConfigureAwait(false);
                return false;
            }
            var name = string.IsNullOrWhiteSpace(req.ViewerName) ? "a BetterVNC viewer" : req.ViewerName.Trim();
            var address = _tcp.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : Peer;
            _server.Log?.Invoke($"{Peer}: pairing requested by {name}");
            bool allowed;
            using (var approveCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                approveCts.CancelAfter(opts.PairingTimeout);
                try { allowed = await opts.PairingApprover.ApproveAsync(name, req.ViewerId, address, approveCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { allowed = false; }
                catch (Exception ex) { _server.Log?.Invoke($"{Peer}: approval dialog failed: {ex.Message}"); allowed = false; }
            }
            if (!allowed)
            {
                _server.Log?.Invoke($"{Peer}: pairing denied");
                var err = AgentMessage.ErrorReply(null, "The pairing was denied in the remote screen.");
                err.Code = AgentErrorCode.PairingDenied;
                await SendAsync(err, default, ct).ConfigureAwait(false);
                return false;
            }
            using var mine = AgentPairing.NewKeyPair();
            string key;
            try { key = AgentPairing.DeriveKey(mine, req.PublicKey); }
            catch (AgentProtocolException ex)
            {
                await SendAsync(AgentMessage.ErrorReply(null, ex.Message), default, ct).ConfigureAwait(false);
                return false;
            }
            opts.ViewerPaired(new PairedViewer { Id = req.ViewerId, Name = name, Key = key, PairedAt = DateTime.Now, Address = address });
            _server.Log?.Invoke($"{Peer}: paired with {name}");
            await SendAsync(new AgentMessage { Type = AgentMessageType.Paired, PublicKey = AgentPairing.ExportPublicKey(mine) }, default, ct).ConfigureAwait(false);
            return true;
        }

        private async Task<AgentFrame?> DispatchAsync(AgentFrame frame, CancellationToken ct)
        {
            var h = frame.Header;
            switch (h.Type)
            {
                case AgentMessageType.Ping:
                    return new AgentFrame(AgentMessage.Of(AgentMessageType.Pong, h.Id));
                case AgentMessageType.GetClipboard:
                    return await _server.HandleGetClipboardAsync(h, ct).ConfigureAwait(false);
                case AgentMessageType.SetClipboard:
                    return await _server.HandleSetClipboardAsync(frame, ct).ConfigureAwait(false);
                case AgentMessageType.Scroll:
                    _server.HandleScroll(h);
                    return null; // fire and forget: a reply per wheel tick would only add latency
                case AgentMessageType.StartVideo:
                    return HandleStartVideo(h);
                case AgentMessageType.SetVideo:
                    return HandleSetVideo(h);
                case AgentMessageType.StopVideo:
                    return HandleStopVideo(h);
                case AgentMessageType.ClipboardChanged:
                case AgentMessageType.Pong:
                    return null; // informational
                default:
                    return new AgentFrame(AgentMessage.ErrorReply(h.Id, $"Unknown message type '{h.Type}'."));
            }
        }
    }
}
