using System.Net;
using System.Net.Sockets;
using System.Text;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

public class InputFramingTests
{
    [Fact]
    public void Pointer_header_is_compact_camel_case_json()
    {
        var msg = new AgentMessage { Type = AgentMessageType.Pointer, X = 10, Y = 20, Width = 1920, Height = 1080, Buttons = AgentButton.Left };
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(msg));
        Assert.Equal("{\"type\":\"pointer\",\"x\":10,\"y\":20,\"buttons\":1,\"width\":1920,\"height\":1080}", json);
    }

    [Fact]
    public async Task Key_frame_round_trips()
    {
        var frame = await RoundTrip(new AgentMessage { Type = AgentMessageType.Key, ScanCode = 0xE01D, VirtualKey = 0xA3, Down = false });
        var h = frame.Header;
        Assert.Equal(AgentMessageType.Key, h.Type);
        Assert.Null(h.Id); // no reply expected
        Assert.Equal(0xE01D, h.ScanCode);
        Assert.Equal(0xA3, h.VirtualKey);
        Assert.False(h.Down);
        Assert.Equal(0, frame.Payload.Length);
    }

    [Fact]
    public async Task Cursor_frame_round_trips_with_its_shape_in_the_payload()
    {
        var pixels = Enumerable.Range(0, 3 * 2 * 4).Select(i => (byte)(i * 7)).ToArray();
        var frame = await RoundTrip(new AgentMessage { Type = AgentMessageType.Cursor, Width = 3, Height = 2, HotX = 1, HotY = 0, Visible = true }, pixels);
        var h = frame.Header;
        Assert.Equal(AgentMessageType.Cursor, h.Type);
        Assert.Equal<(int?, int?, int?, int?, bool?)>((3, 2, 1, 0, true), (h.Width, h.Height, h.HotX, h.HotY, h.Visible));
        Assert.Equal(pixels, frame.Payload.ToArray());
    }

    [Fact]
    public void Headers_with_fields_this_version_does_not_know_still_parse()
    {
        // A newer viewer or agent may add fields; older code must read the ones it knows and skip the rest.
        var json = "{\"type\":\"pointer\",\"x\":5,\"y\":6,\"width\":100,\"height\":50,\"buttons\":2,\"pressure\":0.5,\"tilt\":{\"x\":1,\"y\":[2,3]},\"pen\":null}";
        var h = AgentFraming.DecodeHeader(Encoding.UTF8.GetBytes(json));
        Assert.Equal(AgentMessageType.Pointer, h.Type);
        Assert.Equal<(int?, int?, int?, int?, int?)>((5, 6, 100, 50, AgentButton.Middle), (h.X, h.Y, h.Width, h.Height, h.Buttons));
    }

    [Fact]
    public void Start_video_mentions_the_cursor_only_when_asked()
    {
        var plain = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.StartVideo, Codecs = new[] { AgentVideoCodec.H264 } }));
        Assert.DoesNotContain("cursor", plain);
        var asked = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.StartVideo, Codecs = new[] { AgentVideoCodec.H264 }, Cursor = true }));
        Assert.Contains("\"cursor\":true", asked);
    }

    private static async Task<AgentFrame> RoundTrip(AgentMessage msg, byte[]? payload = null)
    {
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, msg, payload ?? Array.Empty<byte>(), TestContext.Current.CancellationToken);
        ms.Position = 0;
        var frame = await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, TestContext.Current.CancellationToken);
        Assert.NotNull(frame);
        return frame!;
    }
}

public class InputServerTests : IAsyncDisposable
{
    private const string Token = "input-token";
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeInput _input = new() { PointerAndKeys = true };
    private readonly FakeVideoSource _video = new() { SupportsCursor = true, Area = new ScreenArea(-1920, 0, 1920, 1080) };
    private readonly AgentServer _server;
    private readonly List<AgentServer> _others = new();

    public InputServerTests()
    {
        _server = new AgentServer(_clipboard, Options(), _input, _video);
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var s in _others) await s.DisposeAsync();
    }

    private static AgentServerOptions Options() => new() { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 };

    private AgentServer Other(IGuestInput? input, IGuestVideoSource? video)
    {
        var s = new AgentServer(_clipboard, Options(), input, video);
        s.Start();
        _others.Add(s);
        return s;
    }

    private static AgentConnection NewClient(AgentServer server) =>
        new(new AgentConnectionOptions { Host = "127.0.0.1", Port = server.Port, Token = Token, AppName = "tests" });

    private static async Task<AgentConnection> Connected(AgentServer server)
    {
        var c = NewClient(server);
        await c.ConnectAsync(TestContext.Current.CancellationToken);
        return c;
    }

    private static Task Within(Task t) => t.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>The session handles frames one after another, so a pong proves everything sent before it was processed.</summary>
    private static Task Processed(AgentConnection c) => Within(c.PingAsync(TestContext.Current.CancellationToken));

    private static FakeInput.PointerCall At(int x, int y, int width, int height, ScreenArea? area, int pressed, int released) =>
        new((x + 0.5) / width, (y + 0.5) / height, area, pressed, released);

    [Fact]
    public async Task Welcome_announces_input_and_cursor_only_when_the_back_ends_can()
    {
        Assert.Equal(new[] { AgentFeature.Scroll, AgentFeature.Video, AgentFeature.Input, AgentFeature.Cursor }, _server.Features);
        var scrollOnly = Other(new FakeInput(), new FakeVideoSource());
        Assert.Equal(new[] { AgentFeature.Scroll, AgentFeature.Video }, scrollOnly.Features);

        using var full = await Connected(_server);
        Assert.True(full.SupportsInput);
        Assert.True(full.SupportsCursor);

        using var limited = await Connected(scrollOnly);
        Assert.False(limited.SupportsInput);
        Assert.False(limited.SupportsCursor);
        await Assert.ThrowsAsync<NotSupportedException>(() => limited.SendPointerAsync(1, 1, 10, 10, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => limited.SendKeyAsync(0x1E, 0x41, true, TestContext.Current.CancellationToken));
        Assert.True(limited.IsConnected); // refusing locally must not cost the session
    }

    [Fact]
    public async Task Pointer_moves_and_buttons_reach_the_input_as_fractions_and_changes()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        await c.SendPointerAsync(0, 0, 100, 50, 0, ct);
        await c.SendPointerAsync(99, 49, 100, 50, AgentButton.Left, ct);
        await c.SendPointerAsync(50, 25, 100, 50, AgentButton.Left | AgentButton.Right, ct);
        await c.SendPointerAsync(50, 25, 100, 50, 0, ct);
        await c.SendPointerAsync(-5, 70, 100, 50, AgentButton.Forward, ct); // past the edges (a drag out of the window): clamped
        await Processed(c);

        Assert.Equal(new object[]
        {
            At(0, 0, 100, 50, null, 0, 0),
            At(99, 49, 100, 50, null, AgentButton.Left, 0),
            At(50, 25, 100, 50, null, AgentButton.Right, 0),
            At(50, 25, 100, 50, null, 0, AgentButton.Left | AgentButton.Right),
            new FakeInput.PointerCall(0, 1, null, AgentButton.Forward, 0),
        }, _input.Input);
    }

    [Fact]
    public async Task While_streaming_the_pointer_maps_onto_the_part_of_the_screen_the_stream_shows()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        await c.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 4000, 30, ct);
        await c.SendPointerAsync(960, 540, 1920, 1080, 0, ct);
        await c.StopVideoAsync(ct);
        await c.SendPointerAsync(960, 540, 1920, 1080, 0, ct);
        await Processed(c);

        Assert.Equal(new object[]
        {
            At(960, 540, 1920, 1080, new ScreenArea(-1920, 0, 1920, 1080), 0, 0),
            At(960, 540, 1920, 1080, null, 0, 0), // no stream: the whole desktop
        }, _input.Input);
    }

    [Fact]
    public async Task Keys_reach_the_input_in_order()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        await c.SendKeyAsync(0x1E, 0x41, true, ct);     // A
        await c.SendKeyAsync(0x1E, 0x41, true, ct);     // auto-repeat
        await c.SendKeyAsync(0x1E, 0x41, false, ct);
        await c.SendKeyAsync(0xE01D, 0xA3, true, ct);   // Right Ctrl: extended
        await c.SendKeyAsync(0, 0x13, true, ct);        // Pause: no scan code, virtual key only
        await c.SendKeyAsync(0, 0x13, false, ct);
        await c.SendKeyAsync(0xE01D, 0xA3, false, ct);
        await Processed(c);

        Assert.Equal(new object[]
        {
            new FakeInput.KeyCall(0x1E, 0x41, true),
            new FakeInput.KeyCall(0x1E, 0x41, true),
            new FakeInput.KeyCall(0x1E, 0x41, false),
            new FakeInput.KeyCall(0xE01D, 0xA3, true),
            new FakeInput.KeyCall(0, 0x13, true),
            new FakeInput.KeyCall(0, 0x13, false),
            new FakeInput.KeyCall(0xE01D, 0xA3, false),
        }, _input.Input);
    }

    [Fact]
    public async Task Keys_and_buttons_still_down_are_released_when_the_viewer_goes_away()
    {
        var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        await c.SendKeyAsync(0x2A, 0xA0, true, ct);     // Left Shift held
        await c.SendKeyAsync(0x1E, 0x41, true, ct);
        await c.SendKeyAsync(0x1E, 0x41, false, ct);    // released normally: must not be released again
        await c.SendPointerAsync(10, 20, 100, 100, AgentButton.Left, ct); // a drag in progress
        await Processed(c);
        int before = _input.Input.Count;
        c.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_input.Input.Count < before + 2 && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        var released = _input.Input.Skip(before).ToList();
        Assert.Equal(2, released.Count);
        Assert.Contains(new FakeInput.KeyCall(0x2A, 0xA0, false), released);
        Assert.Contains(At(10, 20, 100, 100, null, 0, AgentButton.Left), released);
    }

    [Fact]
    public async Task Malformed_pointer_and_key_messages_are_ignored_and_the_session_goes_on()
    {
        using var raw = await RawSession.Open(_server);
        await raw.Send(new AgentMessage { Type = AgentMessageType.Pointer, X = 1, Y = 1 });                         // no picture size
        await raw.Send(new AgentMessage { Type = AgentMessageType.Pointer, X = 1, Y = 1, Width = 0, Height = 10 }); // empty picture
        await raw.Send(new AgentMessage { Type = AgentMessageType.Key, Down = true });                              // no key at all
        await raw.Send(new AgentMessage { Type = AgentMessageType.Key, ScanCode = 0x1E });                          // neither down nor up
        await raw.Send(new AgentMessage { Type = AgentMessageType.Key, ScanCode = 0x1E, VirtualKey = 0x400, Down = true }); // no such virtual key
        var pong = await raw.Request(AgentMessage.Of(AgentMessageType.Ping, 5));
        Assert.Equal(AgentMessageType.Pong, pong.Type);
        Assert.Empty(_input.Input);
    }

    [Fact]
    public async Task An_agent_without_pointer_input_ignores_pointer_and_key_frames_instead_of_failing()
    {
        // By hand: AgentConnection refuses to send these to such an agent, but a buggy or newer viewer might.
        var scrollOnly = new FakeInput();
        using var raw = await RawSession.Open(Other(scrollOnly, null));
        Assert.True(raw.Welcome.Features is { } f && !f.Contains(AgentFeature.Input));
        await raw.Send(new AgentMessage { Type = AgentMessageType.Pointer, X = 1, Y = 1, Width = 10, Height = 10, Buttons = AgentButton.Left });
        await raw.Send(new AgentMessage { Type = AgentMessageType.Key, ScanCode = 0x1E, VirtualKey = 0x41, Down = true });
        var pong = await raw.Request(AgentMessage.Of(AgentMessageType.Ping, 6));
        Assert.Equal(AgentMessageType.Pong, pong.Type); // not an error frame
        Assert.Empty(scrollOnly.Input);
    }

    [Fact]
    public async Task Unknown_notifications_are_ignored_and_unknown_requests_get_an_error()
    {
        using var raw = await RawSession.Open(_server);
        // An error without an id would end the viewer's connection, so a notification the agent does not know gets nothing.
        await raw.Send(new AgentMessage { Type = "hologram" }, new byte[] { 1, 2, 3 });
        var pong = await raw.Request(AgentMessage.Of(AgentMessageType.Ping, 7));
        Assert.Equal<(string, int?)>((AgentMessageType.Pong, 7), (pong.Type, pong.Id));

        // A request must not be left waiting: it gets an error carrying its id.
        var error = await raw.Request(AgentMessage.Of("hologram", 8));
        Assert.Equal<(string, int?)>((AgentMessageType.Error, 8), (error.Type, error.Id));
        Assert.Contains("hologram", error.Message);
        pong = await raw.Request(AgentMessage.Of(AgentMessageType.Ping, 9));
        Assert.Equal<(string, int?)>((AgentMessageType.Pong, 9), (pong.Type, pong.Id));
    }

    [Fact]
    public async Task Cursor_shapes_reach_a_viewer_that_asked_for_them()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        var received = System.Threading.Channels.Channel.CreateUnbounded<AgentFrame>();
        c.CursorReceived += f => received.Writer.TryWrite(f);
        var info = await c.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 4000, 30, null, cursor: true, ct);
        Assert.True(info.Cursor);
        var session = await _video.NextSession();
        Assert.NotNull(session.OnCursor);

        var pixels = Enumerable.Range(0, 2 * 2 * 4).Select(i => (byte)(255 - i)).ToArray();
        session.OnCursor!(new GuestCursor(true, new CursorImage(2, 2, 1, 0, pixels)));
        var shape = await received.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal<(int?, int?, int?, int?, bool?)>((2, 2, 1, 0, true), (shape.Header.Width, shape.Header.Height, shape.Header.HotX, shape.Header.HotY, shape.Header.Visible));
        Assert.Equal(pixels, shape.Payload.ToArray());

        session.OnCursor(new GuestCursor(false, null));
        var hidden = await received.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(hidden.Header.Visible);
        Assert.Equal(0, hidden.Payload.Length); // the same shape as before: not sent again
        Assert.Null(hidden.Header.Width);
    }

    [Fact]
    public async Task A_shape_that_does_not_match_its_size_is_dropped()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        var received = System.Threading.Channels.Channel.CreateUnbounded<AgentFrame>();
        c.CursorReceived += f => received.Writer.TryWrite(f);
        await c.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 4000, 30, null, cursor: true, ct);
        var session = await _video.NextSession();
        session.OnCursor!(new GuestCursor(true, new CursorImage(4, 4, 0, 0, new byte[10])));
        session.OnCursor(new GuestCursor(true, new CursorImage(1, 1, 0, 0, new byte[] { 1, 2, 3, 4 })));
        var first = await received.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal<(int?, int?)>((1, 1), (first.Header.Width, first.Header.Height));
    }

    [Fact]
    public async Task Without_the_cursor_option_the_source_is_not_asked_for_the_pointer()
    {
        using var c = await Connected(_server);
        var ct = TestContext.Current.CancellationToken;
        var info = await c.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 4000, 30, null, cursor: false, ct);
        Assert.False(info.Cursor);
        Assert.Null((await _video.NextSession()).OnCursor);

        // Asked, but the source cannot: no callback, and the reply says so.
        var noCursor = new FakeVideoSource();
        using var other = await Connected(Other(_input, noCursor));
        var refused = await other.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 4000, 30, null, cursor: true, ct);
        Assert.False(refused.Cursor);
        Assert.Null((await noCursor.NextSession()).OnCursor);
    }

    /// <summary>A hand-driven viewer: authenticates with the token, then sends and reads frames as given.</summary>
    private sealed class RawSession : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private RawSession(TcpClient tcp, AgentMessage welcome)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            Welcome = welcome;
        }

        public AgentMessage Welcome { get; }

        public static async Task<RawSession> Open(AgentServer server)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            var s = tcp.GetStream();
            var hello = await AgentFraming.ReadAsync(s, 65536, default);
            await AgentFraming.WriteAsync(s, new AgentMessage
            {
                Type = AgentMessageType.Hello, Version = 1, Os = "test", Nonce = AgentAuth.NewNonce(), Proof = AgentAuth.Prove(Token, hello!.Header.Nonce!),
            }, default, default);
            var welcome = await AgentFraming.ReadAsync(s, 65536, default);
            Assert.Equal(AgentMessageType.Welcome, welcome!.Header.Type);
            return new RawSession(tcp, welcome.Header);
        }

        public Task Send(AgentMessage msg, byte[]? payload = null) => AgentFraming.WriteAsync(_stream, msg, payload ?? Array.Empty<byte>(), default).AsTask();

        public async Task<AgentMessage> Request(AgentMessage msg)
        {
            await Send(msg);
            var reply = await AgentFraming.ReadAsync(_stream, 65536, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(reply);
            return reply!.Header;
        }

        public void Dispose() => _tcp.Dispose();
    }
}

public class ViewerToleranceTests
{
    [Fact]
    public async Task The_viewer_ignores_messages_and_features_it_does_not_know()
    {
        // A stand-in for a newer agent: announces a feature this viewer has never heard of and sends it notifications of
        // a kind it does not know, with and without payload, before answering a ping.
        const string token = "newer-agent";
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var agent = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            var s = tcp.GetStream();
            await AgentFraming.WriteAsync(s, new AgentMessage { Type = AgentMessageType.Hello, Version = 1, Os = "future", Nonce = AgentAuth.NewNonce() }, default, ct);
            var hello = await AgentFraming.ReadAsync(s, 65536, ct);
            await AgentFraming.WriteAsync(s, new AgentMessage
            {
                Type = AgentMessageType.Welcome, Version = 1, Os = "future", Proof = AgentAuth.Prove(token, hello!.Header.Nonce!),
                Features = new[] { AgentFeature.Input, "teleport" }, Formats = Array.Empty<string>(),
            }, default, ct);
            await AgentFraming.WriteAsync(s, new AgentMessage { Type = "teleport", Message = "whoosh" }, new byte[] { 9, 9, 9 }, ct);
            await AgentFraming.WriteAsync(s, new AgentMessage { Type = "weather" }, default, ct);
            var ping = await AgentFraming.ReadAsync(s, 65536, ct);
            await AgentFraming.WriteAsync(s, AgentMessage.Of(AgentMessageType.Pong, ping!.Header.Id), default, ct);
            try { await AgentFraming.ReadAsync(s, 65536, ct); } catch (IOException) { } // until the viewer hangs up
        }, ct);

        using var c = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Token = token });
        await c.ConnectAsync(ct);
        Assert.Contains("teleport", c.RemoteFeatures);
        Assert.True(c.SupportsInput);
        await c.PingAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(c.IsConnected);
        c.Dispose();
        await agent.WaitAsync(TimeSpan.FromSeconds(5), ct);
        listener.Stop();
    }
}
