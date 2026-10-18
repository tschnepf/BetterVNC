using System.Net;
using System.Text;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

/// <summary>Stand-in for a screen capture: records what was asked and lets a test push frames or end the stream.</summary>
public sealed class FakeVideoSource : IGuestVideoSource
{
    public IReadOnlyList<string> Codecs { get; set; } = new[] { AgentVideoCodec.Jpeg };
    /// <summary>When set, Start throws with this message (capture impossible).</summary>
    public string? FailStart { get; set; }
    /// <summary>When set, sessions report their frames as this part of the desktop (a primary display inside a larger one).</summary>
    public (int X, int Y)? Offset { get; set; }
    public int Starts { get; private set; }
    public FakeSession? Last { get; private set; }
    private readonly System.Threading.Channels.Channel<FakeSession> _sessions = System.Threading.Channels.Channel.CreateUnbounded<FakeSession>();

    /// <summary>The sessions in the order they were started; each call consumes one.</summary>
    public async Task<FakeSession> NextSession() => await _sessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded)
    {
        Starts++;
        if (FailStart != null) throw new InvalidOperationException(FailStart);
        var s = new FakeSession(request, onFrame, onEnded) { Offset = Offset };
        Last = s;
        _sessions.Writer.TryWrite(s);
        return s;
    }

    public sealed class FakeSession : IVideoSession
    {
        private readonly Action<VideoFrame> _onFrame;
        private readonly Action<string?> _onEnded;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _n;

        public FakeSession(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded)
        {
            Request = request;
            Codec = request.Codec;
            BitrateKbps = request.BitrateKbps;
            MaxFps = request.MaxFps;
            _onFrame = onFrame;
            _onEnded = onEnded;
        }

        /// <summary>What the server passed to <see cref="FakeVideoSource.Start"/>.</summary>
        public VideoRequest Request { get; }
        /// <summary>Every <see cref="SetMaxSize"/> call, in order.</summary>
        public List<(int Width, int Height)> MaxSizes { get; } = new();
        public string Codec { get; }
        public int BitrateKbps { get; private set; }
        public int MaxFps { get; private set; }
        public int Width => 1920;
        public int Height => 1080;
        public (int X, int Y)? Offset { get; set; }
        public string EncoderKind => AgentVideoEncoder.Software;
        public int KeyframeRequests { get; private set; }
        public bool IsDisposed => _disposed.Task.IsCompleted;
        public Task Disposed => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Set(int? bitrateKbps, int? maxFps)
        {
            if (bitrateKbps is { } b) BitrateKbps = b;
            if (maxFps is { } f) MaxFps = f;
        }

        public void RequestKeyframe() => KeyframeRequests++;

        public void SetMaxSize(int maxWidth, int maxHeight) => MaxSizes.Add((maxWidth, maxHeight));

        /// <summary>Pushes one frame as the capture thread would.</summary>
        public void Emit(byte[] data) => _onFrame(new VideoFrame(data, true, ++_n * 16_667L, Width, Height, Offset?.X, Offset?.Y));

        /// <summary>The capture died (locked desktop, ...).</summary>
        public void End(string reason) => _onEnded(reason);

        public void Dispose() => _disposed.TrySetResult();
    }
}

public class VideoFramingTests
{
    [Fact]
    public void Start_video_header_lists_codecs_bitrate_and_fps()
    {
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage
        {
            Type = AgentMessageType.StartVideo, Id = 1, Codecs = new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg }, Bitrate = 8000, MaxFps = 60,
        }));
        Assert.Equal("{\"type\":\"startVideo\",\"id\":1,\"codecs\":[\"h264\",\"jpeg\"],\"bitrate\":8000,\"maxFps\":60}", json);
    }

    [Fact]
    public void Viewer_area_travels_as_width_and_height_and_is_absent_without_one()
    {
        var start = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage
        {
            Type = AgentMessageType.StartVideo, Id = 1, Codecs = new[] { AgentVideoCodec.H264 }, Bitrate = 8000, MaxFps = 60, Width = 1600, Height = 1000,
        }));
        Assert.Equal("{\"type\":\"startVideo\",\"id\":1,\"codecs\":[\"h264\"],\"bitrate\":8000,\"maxFps\":60,\"width\":1600,\"height\":1000}", start);
        var set = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.SetVideo, Id = 2, Width = 1280, Height = 800 }));
        Assert.Equal("{\"type\":\"setVideo\",\"id\":2,\"width\":1280,\"height\":800}", set);
        // Re-tuning the bitrate says nothing about the area, so the agent keeps the one it has.
        var retune = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.SetVideo, Id = 3, Bitrate = 4000 }));
        Assert.DoesNotContain("width", retune);
    }

    [Fact]
    public void Welcome_without_video_has_no_codecs()
    {
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.Welcome, Version = 1, Features = new[] { AgentFeature.Scroll } }));
        Assert.DoesNotContain("codecs", json);
        Assert.DoesNotContain("video", json);
    }

    [Fact]
    public async Task Video_frame_round_trips_header_and_payload()
    {
        var header = new AgentMessage { Type = AgentMessageType.VideoFrame, Keyframe = true, Timestamp = 123456, Width = 1280, Height = 720, Sequence = 7 };
        var payload = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, header, payload, default);
        ms.Position = 0;
        var frame = await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, default);
        Assert.NotNull(frame);
        Assert.Equal(AgentMessageType.VideoFrame, frame!.Header.Type);
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(123456, frame.Header.Timestamp);
        Assert.Equal(1280, frame.Header.Width);
        Assert.Equal(720, frame.Header.Height);
        Assert.Equal(7, frame.Header.Sequence);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.Null(frame.Header.OffsetX);
        Assert.Null(frame.Header.OffsetY);
    }

    [Fact]
    public async Task Frame_offset_round_trips_and_is_absent_from_whole_desktop_headers()
    {
        static async Task<(AgentMessage Header, string Json)> RoundTrip(AgentMessage header)
        {
            using var ms = new MemoryStream();
            await AgentFraming.WriteAsync(ms, header, new byte[] { 1 }, default);
            string json = Encoding.UTF8.GetString(ms.ToArray());
            ms.Position = 0;
            var frame = await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, default);
            return (frame!.Header, json);
        }

        // The primary display sits to the right of a monitor that is 1920 wide and 200 px lower than the desktop's top edge.
        var partial = await RoundTrip(new AgentMessage { Type = AgentMessageType.VideoFrame, Width = 2256, Height = 1504, OffsetX = 1920, OffsetY = 200, Sequence = 1 });
        Assert.Equal(1920, partial.Header.OffsetX);
        Assert.Equal(200, partial.Header.OffsetY);
        Assert.Contains("\"offsetX\":1920", partial.Json);
        Assert.Contains("\"offsetY\":200", partial.Json);

        // A primary display at the desktop origin still says so explicitly: 0 is not the same as "the whole desktop".
        var atOrigin = await RoundTrip(new AgentMessage { Type = AgentMessageType.VideoStarted, Width = 2256, Height = 1504, OffsetX = 0, OffsetY = 0 });
        Assert.Equal(0, atOrigin.Header.OffsetX);
        Assert.Equal(0, atOrigin.Header.OffsetY);
        Assert.Contains("\"offsetX\":0", atOrigin.Json);

        // A whole-desktop stream (the single-display case) carries no offset fields at all, as before.
        var whole = await RoundTrip(new AgentMessage { Type = AgentMessageType.VideoFrame, Width = 2256, Height = 1504, Sequence = 1 });
        Assert.Null(whole.Header.OffsetX);
        Assert.Null(whole.Header.OffsetY);
        Assert.DoesNotContain("offset", whole.Json, StringComparison.OrdinalIgnoreCase);
    }
}

public class VideoServerTests : IAsyncDisposable
{
    private const string Token = "video-token";
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeVideoSource _video = new();
    private readonly AgentServer _server;

    public VideoServerTests()
    {
        _server = new AgentServer(_clipboard, new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 }, null, _video);
        _server.Start();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private AgentConnection NewClient() =>
        new(new AgentConnectionOptions { Host = "127.0.0.1", Port = _server.Port, Token = Token, AppName = "tests" });

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Welcome_announces_video_and_codecs()
    {
        Assert.Equal(new[] { AgentFeature.Video }, _server.Features);
        using var client = NewClient();
        await client.ConnectAsync(default);
        Assert.True(client.SupportsVideo);
        Assert.Equal(new[] { AgentVideoCodec.Jpeg }, client.RemoteCodecs);
        Assert.False(client.SupportsScroll);
    }

    [Fact]
    public async Task Server_without_a_source_announces_nothing_and_the_client_refuses_to_ask()
    {
        await using var plain = new AgentServer(new FakeClipboard(), new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 });
        plain.Start();
        Assert.Empty(plain.Features);
        Assert.Null(plain.VideoCodecs);
        using var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = plain.Port, Token = Token });
        await client.ConnectAsync(default);
        Assert.False(client.SupportsVideo);
        Assert.Empty(client.RemoteCodecs);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default));
    }

    [Fact]
    public async Task Start_negotiates_the_first_common_codec_and_frames_flow_to_the_viewer()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var received = new List<AgentFrame>();
        var gotThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VideoFrameReceived += f => { lock (received) { received.Add(f); if (received.Count == 3) gotThree.TrySetResult(); } };

        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg }, 5000, 30, default);
        Assert.Equal(AgentVideoCodec.Jpeg, info.Codec); // the viewer prefers H.264, the agent only has JPEG
        Assert.Equal(5000, info.BitrateKbps);
        Assert.Equal(30, info.MaxFps);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Null(info.OffsetX); // the whole desktop: no placement
        Assert.Null(info.OffsetY);
        Assert.Equal(AgentVideoEncoder.Software, info.EncoderKind);
        Assert.Equal(1, _server.StreamingSessions);

        var session = await _video.NextSession();
        Assert.Equal(5000, session.BitrateKbps);
        session.Emit(new byte[] { 1 });
        session.Emit(new byte[] { 2, 2 });
        session.Emit(new byte[] { 3, 3, 3 });
        await gotThree.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            Assert.Equal(new[] { 1, 2, 3 }, received.Select(f => f.Header.Sequence!.Value));
            Assert.Equal(new byte[] { 3, 3, 3 }, received[2].Payload.ToArray());
            Assert.All(received, f => Assert.Equal(AgentMessageType.VideoFrame, f.Header.Type));
            Assert.All(received, f => Assert.True(f.Header.Keyframe));
            Assert.Equal(1920, received[0].Header.Width);
            Assert.Equal(3 * 16_667L, received[2].Header.Timestamp);
            Assert.All(received, f => Assert.Null(f.Header.OffsetX));
        }

        await client.SetVideoAsync(1000, 15, keyframe: true, default);
        Assert.Equal(1000, session.BitrateKbps);
        Assert.Equal(15, session.MaxFps);
        Assert.Equal(1, session.KeyframeRequests);

        await client.StopVideoAsync(default);
        await session.Disposed;
        Assert.Equal(0, _server.StreamingSessions);
        Assert.True(client.IsConnected); // the clipboard side of the session is untouched
    }

    [Fact]
    public async Task Viewer_area_reaches_the_source_at_start_and_when_it_changes()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, (1600, 1000), default);
        var session = await _video.NextSession();
        Assert.Equal(1600, session.Request.MaxWidth);
        Assert.Equal(1000, session.Request.MaxHeight);
        Assert.Empty(session.MaxSizes);

        await client.SetVideoAreaAsync(1280, 800, default); // the window got smaller
        await client.SetVideoAsync(4000, null, keyframe: false, default); // a re-tune leaves the area alone
        await client.SetVideoAreaAsync(0, 0, default);      // no limit
        await client.SetVideoAreaAsync(-1, 100_000, default); // clamped
        Assert.Equal(new[] { (1280, 800), (0, 0), (0, 16_384) }, session.MaxSizes);
        Assert.Equal(4000, session.BitrateKbps);

        await client.StopVideoAsync(default);
        await session.Disposed;
    }

    [Fact]
    public async Task A_viewer_that_sends_no_area_gets_no_limit()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        var session = await _video.NextSession();
        Assert.Equal(0, session.Request.MaxWidth);
        Assert.Equal(0, session.Request.MaxHeight);
        await client.SetVideoAsync(null, null, keyframe: true, default);
        Assert.Empty(session.MaxSizes);
    }

    /// <summary>A capture that cannot scale (the Windows agent's): it leaves <see cref="IVideoSession.SetMaxSize"/> to the default.</summary>
    private sealed class FixedSizeSource : IGuestVideoSource
    {
        public IReadOnlyList<string> Codecs { get; } = new[] { AgentVideoCodec.H264 };
        public VideoRequest? Request { get; private set; }

        public IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded)
        {
            Request = request;
            return new Session();
        }

        private sealed class Session : IVideoSession
        {
            public string Codec => AgentVideoCodec.H264;
            public int BitrateKbps => 8000;
            public int MaxFps => 60;
            public int Width => 2560;
            public int Height => 1440;
            public string EncoderKind => AgentVideoEncoder.Hardware;
            public void Set(int? bitrateKbps, int? maxFps) { }
            public void RequestKeyframe() { }
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_source_that_cannot_scale_takes_the_area_and_streams_at_its_own_size()
    {
        var source = new FixedSizeSource();
        await using var server = new AgentServer(new FakeClipboard(), new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 }, null, source);
        server.Start();
        using var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = server.Port, Token = Token });
        await client.ConnectAsync(default);
        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.H264 }, 8000, 60, (1280, 720), default);
        Assert.Equal((1280, 720), (source.Request!.MaxWidth, source.Request.MaxHeight));
        Assert.Equal((2560, 1440), (info.Width, info.Height)); // the viewer scales, as before
        await client.SetVideoAreaAsync(800, 600, default); // accepted; nothing changes
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Stream_of_part_of_the_desktop_says_where_its_frames_sit()
    {
        _video.Offset = (1920, 0); // the primary display to the right of a 1920-wide monitor, as on a docked laptop
        using var client = NewClient();
        await client.ConnectAsync(default);
        var received = new TaskCompletionSource<AgentFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VideoFrameReceived += f => received.TrySetResult(f);

        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        Assert.Equal(1920, info.OffsetX);
        Assert.Equal(0, info.OffsetY);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);

        var session = await _video.NextSession();
        session.Emit(new byte[] { 9 });
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1920, frame.Header.OffsetX);
        Assert.Equal(0, frame.Header.OffsetY);
        Assert.Equal(1920, frame.Header.Width);
        Assert.Equal(1080, frame.Header.Height);

        await client.StopVideoAsync(default);
        await session.Disposed;
    }

    [Fact]
    public async Task Agent_offering_both_codecs_gives_the_viewer_its_first_preference()
    {
        _video.Codecs = new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg };
        using var client = NewClient();
        await client.ConnectAsync(default);
        Assert.Equal(new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg }, client.RemoteCodecs);

        // The viewer prefers H.264: both have it, H.264 wins even though the agent would also do JPEG.
        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg }, 8000, 60, default);
        Assert.Equal(AgentVideoCodec.H264, info.Codec);
        var session = await _video.NextSession();
        Assert.Equal(AgentVideoCodec.H264, session.Codec);

        // A viewer that only decodes JPEG still gets JPEG from the same agent.
        var jpeg = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        Assert.Equal(AgentVideoCodec.Jpeg, jpeg.Codec);
        await session.Disposed;
        Assert.Equal(AgentVideoCodec.Jpeg, (await _video.NextSession()).Codec);

        // The viewer's order decides, not the agent's.
        var jpegFirst = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg, AgentVideoCodec.H264 }, 8000, 60, default);
        Assert.Equal(AgentVideoCodec.Jpeg, jpegFirst.Codec);
    }

    [Fact]
    public void Viewer_side_preference_lists_h264_before_jpeg()
    {
        // The viewer's AgentClient.DecodableCodecs is { h264, jpeg }; the probe uses the same list. Keep the protocol
        // constants it is built from stable.
        Assert.Equal("h264", AgentVideoCodec.H264);
        Assert.Equal("jpeg", AgentVideoCodec.Jpeg);
    }

    [Fact]
    public async Task No_common_codec_is_refused()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => client.StartVideoAsync(new[] { AgentVideoCodec.H264 }, 8000, 60, default));
        Assert.Contains("codec", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _video.Starts);
        Assert.Equal(0, _server.StreamingSessions);
    }

    [Fact]
    public async Task Capture_failure_reaches_the_viewer_as_an_error()
    {
        _video.FailStart = "no interactive desktop";
        using var client = NewClient();
        await client.ConnectAsync(default);
        var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default));
        Assert.Contains("no interactive desktop", ex.Message);
        Assert.Equal(0, _server.StreamingSessions);
    }

    [Fact]
    public async Task Bitrate_and_frame_rate_are_clamped()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 1, 1000, default);
        Assert.Equal(250, info.BitrateKbps);
        Assert.Equal(120, info.MaxFps);
    }

    [Fact]
    public async Task Source_ending_on_its_own_tells_the_viewer_and_frees_the_session()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VideoStopped += r => stopped.TrySetResult(r);

        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        var first = await _video.NextSession();
        first.End("desktop locked");
        Assert.Equal("desktop locked", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await first.Disposed;
        await Eventually(() => _server.StreamingSessions == 0, "the stream to be released");

        // A new start works, with a fresh capture session.
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        var second = await _video.NextSession();
        Assert.NotSame(first, second);
        Assert.Equal(2, _video.Starts);
    }

    [Fact]
    public async Task Starting_again_replaces_the_running_stream()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        var first = await _video.NextSession();
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 2000, 30, default);
        var second = await _video.NextSession();
        await first.Disposed;
        Assert.False(second.IsDisposed);
        Assert.Equal(2000, second.BitrateKbps);
        Assert.Equal(1, _server.StreamingSessions);
    }

    [Fact]
    public async Task Viewer_going_away_stops_the_capture()
    {
        var client = NewClient();
        await client.ConnectAsync(default);
        await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        var session = await _video.NextSession();
        client.Dispose();
        await session.Disposed;
        await Eventually(() => _server.StreamingSessions == 0 && _server.SessionCount == 0, "the session to close");
    }

    [Fact]
    public async Task Set_without_a_stream_is_an_error()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => client.SetVideoAsync(1000, null, false, default));
        Assert.Contains("No video stream", ex.Message);
    }
}
