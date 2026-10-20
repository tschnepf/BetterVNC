using System.Buffers.Binary;
using System.Net;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

public class RepairPayloadTests
{
    private const int W = 100, H = 150;

    /// <summary>A BGRA picture with some padding after each row and a pattern no rectangle could get right by accident.</summary>
    private static (byte[] Pixels, int Stride) Picture()
    {
        int stride = W * 4 + 16;
        var pixels = new byte[stride * H];
        new Random(42).NextBytes(pixels);
        return (pixels, stride);
    }

    [Fact]
    public void Rectangles_round_trip_exactly_and_nothing_else_is_painted()
    {
        var (source, stride) = Picture();
        var rects = new[] { new RepairRect(3, 5, 40, 90), new RepairRect(60, 0, 40, 150), new RepairRect(90, 140, 30, 30) };
        var payload = RepairPayload.Encode(source, stride, W, H, rects, maxThreads: 4);

        var target = new byte[W * H * 4];
        var painted = RepairPayload.Decode(payload, target, W, H, maxThreads: 4);

        // Cut into bands of at most 64 rows, and the last rectangle clipped to the picture.
        Assert.All(painted, r => Assert.InRange(r.Height, 1, RepairPayload.MaxRows));
        Assert.Equal(2 + 3 + 1, painted.Length);
        bool Inside(int x, int y) => (x >= 3 && x < 43 && y >= 5 && y < 95) || x >= 60 || (x >= 90 && y >= 140);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int s = y * stride + x * 4, t = (y * W + x) * 4;
                if (Inside(x, y))
                {
                    Assert.Equal(source[s], target[t]);
                    Assert.Equal(source[s + 1], target[t + 1]);
                    Assert.Equal(source[s + 2], target[t + 2]);
                    Assert.Equal(255, target[t + 3]);
                }
                else Assert.Equal(0, target[t] | target[t + 1] | target[t + 2] | target[t + 3]);
            }
    }

    [Fact]
    public void Screen_content_compresses_well()
    {
        // A flat background with a little text-like detail: what a still desktop mostly is.
        var pixels = new byte[1920 * 1080 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 30; pixels[i + 1] = 30; pixels[i + 2] = 30; }
        for (int y = 100; y < 1000; y += 18)
            for (int x = 60; x < 1500; x += 3) pixels[(y * 1920 + x) * 4 + 1] = 200;
        var payload = RepairPayload.Encode(pixels, 1920 * 4, 1920, 1080, new[] { new RepairRect(0, 0, 1920, 1080) }, Environment.ProcessorCount);
        Assert.InRange(payload.Length, 1, 1920 * 1080 * 3 / 50);
    }

    [Fact]
    public void Nothing_to_repair_is_an_empty_payload_that_paints_nothing()
    {
        var (source, stride) = Picture();
        var payload = RepairPayload.Encode(source, stride, W, H, new[] { new RepairRect(200, 200, 10, 10) });
        Assert.Empty(RepairPayload.Decode(payload, new byte[W * H * 4], W, H));
    }

    [Fact]
    public void A_rectangle_outside_the_picture_is_refused()
    {
        var (source, stride) = Picture();
        var payload = RepairPayload.Encode(source, stride, W, H, new[] { new RepairRect(0, 0, W, 10) });
        // The viewer's picture is smaller than the agent's: the rectangle no longer fits.
        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(payload, new byte[50 * H * 4], 50, H));
    }

    [Fact]
    public void Truncated_or_corrupt_payloads_are_refused()
    {
        var (source, stride) = Picture();
        var payload = RepairPayload.Encode(source, stride, W, H, new[] { new RepairRect(10, 10, 20, 20) });
        var target = new byte[W * H * 4];

        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(payload.AsMemory(0, payload.Length - 5), target, W, H));
        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(payload.AsMemory(0, 8), target, W, H));

        var wrongVersion = (byte[])payload.Clone();
        wrongVersion[0] = 99;
        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(wrongVersion, target, W, H));

        var garbled = (byte[])payload.Clone();
        for (int i = 3 + 12 + 2; i < garbled.Length; i++) garbled[i] ^= 0x5A; // past the zlib header: the deflate data
        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(garbled, target, W, H));

        // A rectangle header that claims more pixels than its zlib data holds.
        var tooFew = (byte[])payload.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(tooFew.AsSpan(3 + 6), 21);
        Assert.Throws<AgentProtocolException>(() => RepairPayload.Decode(tooFew, target, W, H));
    }
}

public class RepairServerTests : IAsyncDisposable
{
    private const string Token = "repair-token";
    private readonly FakeVideoSource _video = new() { SupportsRepair = true };
    private readonly AgentServer _server;

    public RepairServerTests()
    {
        _server = new AgentServer(new FakeClipboard(), new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 }, null, _video);
        _server.Start();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private async Task<AgentConnection> ConnectAsync()
    {
        var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = _server.Port, Token = Token, AppName = "tests" });
        await client.ConnectAsync(default);
        return client;
    }

    [Fact]
    public async Task Repairs_are_announced_asked_for_and_arrive_in_order_with_the_frames()
    {
        Assert.Contains(AgentFeature.Repair, _server.Features);
        using var client = await ConnectAsync();
        Assert.True(client.SupportsRepair);
        var received = new List<AgentFrame>();
        var gotFour = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VideoFrameReceived += f => { lock (received) { received.Add(f); if (received.Count == 4) gotFour.TrySetResult(); } };

        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, null, cursor: false, repair: true, default);
        Assert.True(info.Repair);
        var session = await _video.NextSession();
        Assert.True(session.Request.Repair);

        session.Emit(new byte[] { 1 });
        session.EmitRepair(new byte[] { 9, 9 }, whole: true);
        session.EmitDelta(new byte[] { 2 });
        session.EmitRepair(new byte[] { 8 }, whole: false);
        await gotFour.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            Assert.Equal(new[] { AgentMessageType.VideoFrame, AgentMessageType.VideoRepair, AgentMessageType.VideoFrame, AgentMessageType.VideoRepair },
                received.Select(f => f.Header.Type));
            // Frames and repairs are counted apart, so a viewer's gap check on either is not upset by the other.
            Assert.Equal(new[] { 1, 1, 2, 2 }, received.Select(f => f.Header.Sequence!.Value));
            Assert.Equal(new bool?[] { true, true, false, false }, received.Select(f => f.Header.Keyframe));
            Assert.Equal(new byte[] { 9, 9 }, received[1].Payload.ToArray());
            Assert.All(received, f => Assert.Equal(1920, f.Header.Width));
        }
    }

    [Fact]
    public async Task A_viewer_that_does_not_ask_gets_no_repairs()
    {
        using var client = await ConnectAsync();
        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, default);
        Assert.False(info.Repair);
        Assert.False((await _video.NextSession()).Request.Repair);
    }

    [Fact]
    public async Task A_source_without_repairs_is_not_asked_even_when_the_viewer_wants_them()
    {
        var plain = new FakeVideoSource();
        await using var server = new AgentServer(new FakeClipboard(), new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 }, null, plain);
        server.Start();
        Assert.DoesNotContain(AgentFeature.Repair, server.Features);
        using var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = server.Port, Token = Token });
        await client.ConnectAsync(default);
        Assert.False(client.SupportsRepair);

        var info = await client.StartVideoAsync(new[] { AgentVideoCodec.Jpeg }, 8000, 60, null, cursor: false, repair: true, default);
        Assert.False(info.Repair);
        Assert.False((await plain.NextSession()).Request.Repair);
    }
}
