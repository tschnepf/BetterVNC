using System.Collections.Concurrent;
using System.Text;
using BetterVnc.Agent.Protocol;
using BetterVnc.Media;
using BetterVnc.Platform;
using BetterVnc.Protocol;
using BetterVnc.Protocol.Decoders;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Core.Sessions;

namespace BetterVnc.Viewer.Core.Tests;

/// <summary>The agent's video channel, with frames and stops sent by the test.</summary>
public sealed class FakeAgentStream : IAgentStream
{
    private int _keyframeRequests;

    public AgentConnection.VideoStartInfo? StreamInfo { get; set; }
    public event Action<AgentFrame>? StreamFrame;
    public event Action<string?>? StreamStopped;

    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int KeyframeRequests => Volatile.Read(ref _keyframeRequests);
    public ConcurrentQueue<(int Width, int Height)> Areas { get; } = new();

    public Task<AgentConnection.VideoStartInfo> StartStreamAsync(int bitrateKbps, int maxFps, (int Width, int Height)? area = null)
    {
        Starts++;
        StreamInfo = new AgentConnection.VideoStartInfo(AgentVideoCodec.H264, bitrateKbps, maxFps, 64, 48, "fake");
        return Task.FromResult(StreamInfo);
    }

    public Task SetStreamAreaAsync(int width, int height)
    {
        Areas.Enqueue((width, height));
        return Task.CompletedTask;
    }

    public Task SetStreamAsync(int? bitrateKbps, int? maxFps, bool keyframe = false)
    {
        if (keyframe) Interlocked.Increment(ref _keyframeRequests);
        return Task.CompletedTask;
    }

    public Task StopStreamAsync()
    {
        StreamInfo = null;
        Stops++;
        return Task.CompletedTask;
    }

    /// <summary>An H.264 frame arrives, as from the agent's receive thread. Its payload starts with <paramref name="sequence"/>.</summary>
    public void SendFrame(int sequence, bool keyframe) =>
        StreamFrame?.Invoke(new AgentFrame(
            new AgentMessage { Type = AgentMessageType.VideoFrame, Codec = AgentVideoCodec.H264, Sequence = sequence, Keyframe = keyframe, Timestamp = sequence * 16_667L },
            new byte[] { (byte)sequence, 0, 0, 1 }));

    /// <summary>The agent ends the stream; call on the UI thread, where <see cref="AgentClient"/> raises it.</summary>
    public void StopFromAgent(string reason)
    {
        StreamInfo = null;
        StreamStopped?.Invoke(reason);
    }
}

/// <summary>The RFB side of the picture: records the update pacing and the full refreshes asked for.</summary>
public sealed class FakeRfbPicture : IRfbPicture
{
    private int _refreshes;

    public bool IsConnected { get; set; } = true;
    public TimeSpan UpdateInterval { get; set; }
    public int Refreshes => Volatile.Read(ref _refreshes);
    public void RefreshPicture() => Interlocked.Increment(ref _refreshes);
}

/// <summary>A picture surface that accepts every frame on its "GPU" path and counts what it was asked to do.</summary>
public sealed class FakeVideoSurface : IVideoSurface
{
    private int _framebufferShown, _framesShown;

    public (int Width, int Height)? PictureArea { get; set; } = (800, 600);
    public event Action? PictureAreaChanged;
    public string GpuVideoStatus => "fake surface";
    public bool HasGpuVideo => false;
    public string CpuPathName => "test";
    public ConcurrentQueue<FakeH264Decoder> Decoders { get; } = new();
    public int FramebufferShown => Volatile.Read(ref _framebufferShown);
    public int FramesShown => Volatile.Read(ref _framesShown);

    public bool EnableGpuVideo() => true;
    public void ShowFramebuffer() => Interlocked.Increment(ref _framebufferShown);

    public IH264Decoder CreateH264Decoder(bool hardware)
    {
        var decoder = new FakeH264Decoder();
        Decoders.Enqueue(decoder);
        return decoder;
    }

    public bool PresentVideoFrame(in DecodedFrame frame, (int X, int Y)? offset = null)
    {
        Interlocked.Increment(ref _framesShown);
        return true;
    }

    public bool PresentVideoFrame(byte[] bgrx, int width, int height, (int X, int Y)? offset = null)
    {
        Interlocked.Increment(ref _framesShown);
        return true;
    }

    public void PresentExternalFrame(byte[] bgrx, int width, int height, Action<byte[]>? release = null, (int X, int Y)? offset = null)
    {
        Interlocked.Increment(ref _framesShown);
        release?.Invoke(bgrx);
    }

    public void ChangePictureArea(int width, int height)
    {
        PictureArea = (width, height);
        PictureAreaChanged?.Invoke();
    }
}

/// <summary>Turns every access unit into a 2×2 grey picture and remembers the first payload byte (the test's sequence number).</summary>
public sealed class FakeH264Decoder : IH264Decoder
{
    public string Name => "fake decoder";
    public bool IsHardware => false;
    public string? HardwareFailure => null;
    public ConcurrentQueue<int> Decoded { get; } = new();
    public bool Disposed { get; private set; }

    public unsafe int Decode(ReadOnlySpan<byte> annexB, long timestampMicros, DecodedFrameHandler onFrame)
    {
        Decoded.Enqueue(annexB[0]);
        byte* planes = stackalloc byte[6];
        new Span<byte>(planes, 6).Fill(128);
        var frame = new DecodedFrame((IntPtr)planes, (IntPtr)(planes + 4), 2, 2, 2, timestampMicros);
        onFrame(in frame);
        return 1;
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeJpegDecoder : IJpegDecoder
{
    public byte[] DecodeToBgrx(ReadOnlySpan<byte> jpeg, int width, int height) => new byte[width * height * 4];
}

/// <summary>An in-memory clipboard that raises <see cref="Changed"/> on the thread pool, as the OS listener does.</summary>
public sealed class FakeLocalClipboard : ILocalClipboard
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _data = new();
    private List<string>? _files;
    private int _sequence = 1;

    public event Action? Changed;
    public string OsName => "fake";
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    public string? Text
    {
        get { lock (_lock) return _data.TryGetValue(AgentFormat.Text, out var t) ? Encoding.UTF8.GetString(t) : null; }
    }

    /// <summary>Another application copies text.</summary>
    public void CopyText(string text) => SetData(AgentFormat.Text, Encoding.UTF8.GetBytes(text));

    public string? GetChangeToken() { lock (_lock) return _sequence.ToString(); }

    public IReadOnlyList<string> GetFormats()
    {
        lock (_lock)
        {
            var formats = _data.Keys.ToList();
            if (_files != null) formats.Add(AgentFormat.Files);
            return formats;
        }
    }

    public byte[]? GetData(string format) { lock (_lock) return _data.TryGetValue(format, out var d) ? d : null; }

    public IReadOnlyList<string>? GetFilePaths() { lock (_lock) return _files; }

    public void SetData(string format, byte[] data)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = null;
            _data[format] = data;
            _sequence++;
        }
        RaiseChanged();
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = paths.ToList();
            _sequence++;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
}

public sealed class FakeAudioSink : IAudioSink
{
    public int Starts { get; private set; }
    public void Start(int sampleRate, int channels, int bits) => Starts++;
    public void Feed(byte[] pcm) { }
    public void Stop() { }
    public void Dispose() { }
}

/// <summary>Reversible "protection", marked like a real store's values.</summary>
public sealed class TestSecretStore : ISecretStore
{
    public string? Protect(string? plain) => string.IsNullOrEmpty(plain) ? null : "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

    public string? Unprotect(string? protectedValue) =>
        protectedValue is { } v && v.StartsWith("test:") ? Encoding.UTF8.GetString(Convert.FromBase64String(v[5..])) : null;
}

/// <summary>The session window: remembers what the controller showed and answers its prompts from a script.</summary>
public sealed class FakeSessionView : ISessionView
{
    public FakeVideoSurface Surface { get; } = new();
    public IVideoSurface Video => Surface;
    public double FramesPerSecond => 0;

    public string? OverlayTitle { get; private set; }
    public string? OverlayDetail { get; private set; }
    public bool OverlayButtons { get; private set; }
    public bool OverlayVisible { get; private set; }
    private readonly List<string> _status = new();
    /// <summary>Every status line, oldest first (a copy: the connection's log lines arrive while a test reads).</summary>
    public IReadOnlyList<string> StatusLog { get { lock (_status) return _status.ToArray(); } }
    public string? Status => StatusLog.LastOrDefault();
    public string? Statistics { get; private set; }
    public string? Title { get; private set; }
    public VncClient? Attached { get; private set; }
    public IScrollInjector? ScrollInjector { get; private set; }
    public bool MacKeyboard { get; private set; }
    public bool? AudioAvailable { get; private set; }
    public int PictureMenuRefreshes { get; private set; }
    public List<string> Prompts { get; } = new();
    public List<(string Title, string Text)> Messages { get; } = new();

    /// <summary>Answers to credential prompts, in order; null cancels. An empty queue cancels too.</summary>
    public Queue<(VncCredentials? Credentials, bool Remember)> PromptAnswers { get; } = new();

    public void ShowOverlay(string title, string detail, bool showButtons)
    {
        OverlayTitle = title;
        OverlayDetail = detail;
        OverlayButtons = showButtons;
        OverlayVisible = true;
    }

    public void HideOverlay() => OverlayVisible = false;
    public void SetStatus(string text) { lock (_status) _status.Add(text); }
    public void SetStatistics(string text) => Statistics = text;
    public void SetTitle(string text) => Title = text;
    public void AttachSession(VncClient client) => Attached = client;
    public void DetachSession() => Attached = null;
    public void FitToRemote(int width, int height) { }
    public void SetScrollInjector(IScrollInjector? injector) => ScrollInjector = injector;
    public void UseMacKeyboard() => MacKeyboard = true;
    public void ReleaseKeys() { }
    public void RefreshPictureMenu() => PictureMenuRefreshes++;
    public void SetAudioAvailable(bool available) => AudioAvailable = available;
    public void Beep() { }

    public VncCredentials? PromptCredentials(CredentialKind kind, string prompt, string? username, out bool remember)
    {
        Prompts.Add(prompt);
        var (credentials, rememberThem) = PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : (null, false);
        remember = rememberThem;
        return credentials;
    }

    public void ShowMessage(string title, string text, bool warning) => Messages.Add((title, text));
}
