using System.Net;
using System.Net.Sockets;
using System.Text;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

public class ScrollFramingTests
{
    [Fact]
    public void Scroll_header_carries_pixels_phase_and_wheel_units_in_camel_case()
    {
        var msg = new AgentMessage { Type = AgentMessageType.Scroll, Dx = 0, Dy = -48.5, Phase = AgentScrollPhase.Begin, WheelDy = -120 };
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(msg));
        Assert.Equal("{\"type\":\"scroll\",\"dx\":0,\"dy\":-48.5,\"phase\":\"begin\",\"wheelDy\":-120}", json);
    }

    [Fact]
    public async Task Scroll_frame_round_trips_with_optional_fields()
    {
        var msg = new AgentMessage
        {
            Type = AgentMessageType.Scroll,
            Dx = 12.25, Dy = -96, X = 640, Y = 480, Phase = AgentScrollPhase.Continue, WheelDx = 30.625, WheelDy = -240,
        };
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, msg, default, default);
        ms.Position = 0;
        var frame = await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, default);
        Assert.NotNull(frame);
        var h = frame!.Header;
        Assert.Equal(AgentMessageType.Scroll, h.Type);
        Assert.Null(h.Id); // no reply expected
        Assert.Equal(12.25, h.Dx);
        Assert.Equal(-96, h.Dy);
        Assert.Equal(640, h.X);
        Assert.Equal(480, h.Y);
        Assert.Equal(AgentScrollPhase.Continue, h.Phase);
        Assert.Equal(30.625, h.WheelDx);
        Assert.Equal(-240, h.WheelDy);
        Assert.Equal(0, frame.Payload.Length);
    }

    [Fact]
    public void End_of_gesture_omits_everything_but_type_and_phase()
    {
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.Scroll, Dx = 0, Dy = 0, Phase = AgentScrollPhase.End }));
        Assert.Equal("{\"type\":\"scroll\",\"dx\":0,\"dy\":0,\"phase\":\"end\"}", json);
    }

    [Fact]
    public void Welcome_features_are_optional_and_absent_on_old_agents()
    {
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(new AgentMessage { Type = AgentMessageType.Welcome, Version = 1, Formats = Array.Empty<string>() }));
        Assert.DoesNotContain("features", json);
        var withFeature = new AgentMessage { Type = AgentMessageType.Welcome, Version = 1, Features = new[] { AgentFeature.Scroll } };
        Assert.Contains("\"features\":[\"scroll\"]", Encoding.UTF8.GetString(AgentFraming.EncodeHeader(withFeature)));
    }
}

public class ScrollServerTests : IAsyncDisposable
{
    private const string Token = "scroll-token";
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeInput _input = new();
    private readonly AgentServer _withInput;
    private readonly AgentServer _withoutInput;

    public ScrollServerTests()
    {
        _withInput = new AgentServer(_clipboard, new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 }, _input);
        _withoutInput = new AgentServer(_clipboard, new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 });
        _withInput.Start();
        _withoutInput.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _withInput.DisposeAsync();
        await _withoutInput.DisposeAsync();
    }

    private static AgentConnection NewClient(AgentServer server) =>
        new(new AgentConnectionOptions { Host = "127.0.0.1", Port = server.Port, Token = Token, AppName = "tests" });

    private static Task<T> Within<T>(Task<T> t) => t.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Welcome_announces_scroll_only_when_the_agent_can_inject()
    {
        Assert.Equal(new[] { AgentFeature.Scroll }, _withInput.Features);
        Assert.Empty(_withoutInput.Features);

        using var yes = NewClient(_withInput);
        await yes.ConnectAsync(default);
        Assert.True(yes.SupportsScroll);
        Assert.Equal(new[] { AgentFeature.Scroll }, yes.RemoteFeatures);

        using var no = NewClient(_withoutInput);
        await no.ConnectAsync(default);
        Assert.False(no.SupportsScroll);
        Assert.Empty(no.RemoteFeatures);
        await Assert.ThrowsAsync<NotSupportedException>(() => no.ScrollAsync(0, -48, null, null, AgentScrollPhase.Begin, null, null, default));
        Assert.True(no.IsConnected); // refusing locally must not cost the session
    }

    [Fact]
    public async Task Scroll_messages_reach_the_input_in_order_with_all_fields()
    {
        using var client = NewClient(_withInput);
        await client.ConnectAsync(default);

        await client.ScrollAsync(0, -48, 100, 200, AgentScrollPhase.Begin, 0, -120, default);
        await client.ScrollAsync(16.5, 0, 100, 200, AgentScrollPhase.Continue, 41.25, 0, default);
        await client.ScrollAsync(0, 0, 100, 200, AgentScrollPhase.End, 0, 0, default);
        await Within(client.PingAsync(default)); // the session handles frames one after another, so the pong proves the scrolls were processed

        var calls = _input.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal(new FakeInput.Call(0, -48, 0, -120, AgentScrollPhase.Begin), calls[0]);
        Assert.Equal(new FakeInput.Call(16.5, 0, 41.25, 0, AgentScrollPhase.Continue), calls[1]);
        Assert.Equal(new FakeInput.Call(0, 0, 0, 0, AgentScrollPhase.End), calls[2]);
    }

    [Fact]
    public async Task Scroll_sends_no_reply_and_a_failing_injector_does_not_end_the_session()
    {
        using var client = NewClient(_withInput);
        await client.ConnectAsync(default);
        var logged = new List<string>();
        _withInput.Log += m => { lock (logged) logged.Add(m); };

        _input.ThrowOnce = new InvalidOperationException("no permission");
        await client.ScrollAsync(0, -10, null, null, null, null, null, default);
        await client.ScrollAsync(0, -10, null, null, null, null, null, default);
        await Within(client.PingAsync(default));

        Assert.True(client.IsConnected);
        Assert.Equal(2, _input.Calls.Count);
        lock (logged) Assert.Contains(logged, m => m.Contains("Scroll failed") && m.Contains("no permission"));
    }

    [Fact]
    public async Task Server_without_input_ignores_a_scroll_frame_instead_of_failing()
    {
        // Talk to the server by hand: AgentConnection refuses to send scroll to such an agent, but a buggy or older viewer might.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _withoutInput.Port);
        var s = tcp.GetStream();
        var hello = await AgentFraming.ReadAsync(s, 65536, default);
        Assert.Equal(AgentMessageType.Hello, hello!.Header.Type);
        await AgentFraming.WriteAsync(s, new AgentMessage
        {
            Type = AgentMessageType.Hello, Version = 1, Os = "test", Nonce = AgentAuth.NewNonce(), Proof = AgentAuth.Prove(Token, hello.Header.Nonce!),
        }, default, default);
        var welcome = await AgentFraming.ReadAsync(s, 65536, default);
        Assert.Equal(AgentMessageType.Welcome, welcome!.Header.Type);
        Assert.True(welcome.Header.Features is null or { Length: 0 }, "no features expected");

        await AgentFraming.WriteAsync(s, new AgentMessage { Type = AgentMessageType.Scroll, Dx = 0, Dy = -48, Phase = AgentScrollPhase.Begin }, default, default);
        await AgentFraming.WriteAsync(s, AgentMessage.Of(AgentMessageType.Ping, 42), default, default);
        var reply = await AgentFraming.ReadAsync(s, 65536, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(reply);
        Assert.Equal(AgentMessageType.Pong, reply!.Header.Type); // not an error frame for the scroll
        Assert.Equal(42, reply.Header.Id);
        Assert.Empty(_input.Calls);
    }
}
