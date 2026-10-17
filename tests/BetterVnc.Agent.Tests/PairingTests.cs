using System.Collections.Concurrent;
using System.Net;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

/// <summary>Records what the agent asked and answers with a preset decision.</summary>
public sealed class FakeApprover : IPairingApprover
{
    public bool Allow { get; set; } = true;
    public TimeSpan Delay { get; set; }
    public List<(string name, string id, string address)> Requests { get; } = new();

    public async Task<bool> ApproveAsync(string viewerName, string viewerId, string peerAddress, CancellationToken ct)
    {
        lock (Requests) Requests.Add((viewerName, viewerId, peerAddress));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return Allow;
    }
}

public class PairingTests : IAsyncDisposable
{
    private const string Token = "optional-token";
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeApprover _approver = new();
    private readonly ConcurrentDictionary<string, PairedViewer> _viewers = new();
    private AgentServer? _server;

    private AgentServer StartServer(string? token = null, bool allowPairing = true, TimeSpan? pairingTimeout = null)
    {
        _server = new AgentServer(_clipboard, new AgentServerOptions
        {
            Token = token,
            FindViewer = id => _viewers.TryGetValue(id, out var v) ? v : null,
            ViewerPaired = v => _viewers[v.Id] = v,
            PairingApprover = _approver,
            AllowPairing = allowPairing,
            PairingTimeout = pairingTimeout ?? TimeSpan.FromSeconds(10),
            ListenAddress = IPAddress.Loopback,
            Port = 0,
        });
        _server.Start();
        return _server;
    }

    private AgentConnection Client(string viewerId = "viewer-1", string? pairKey = null, string? token = null) =>
        new(new AgentConnectionOptions
        {
            Host = "127.0.0.1",
            Port = _server!.Port,
            ViewerId = viewerId,
            ViewerName = "TIM-PC",
            PairKey = pairKey,
            Token = token,
            PairingTimeout = TimeSpan.FromSeconds(10),
        });

    public async ValueTask DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_derives_the_same_key_on_both_sides_and_the_connection_is_usable()
    {
        StartServer();
        using var client = Client();
        await client.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(client.IsConnected);
        Assert.NotNull(client.PairedKey);
        Assert.True(_viewers.TryGetValue("viewer-1", out var stored));
        Assert.Equal(client.PairedKey, stored!.Key);
        Assert.Equal("TIM-PC", stored.Name);
        Assert.Equal("127.0.0.1", stored.Address);
        Assert.Single(_approver.Requests);
        Assert.Equal(("TIM-PC", "viewer-1", "127.0.0.1"), _approver.Requests[0]);

        // The paired connection works like any other.
        _clipboard.Put(AgentFormat.Text, "hello"u8.ToArray());
        var frame = await client.GetClipboardAsync(AgentFormat.Text, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello"u8.ToArray(), frame!.Payload.ToArray());
    }

    [Fact]
    public async Task Paired_key_authenticates_later_connections_without_a_dialog()
    {
        StartServer();
        string key;
        using (var first = Client())
        {
            await first.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            key = first.PairedKey!;
        }
        using var second = Client(pairKey: key);
        await second.ConnectAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(second.IsConnected);
        Assert.Single(_approver.Requests); // no second dialog
        Assert.NotNull(_viewers["viewer-1"].LastSeen);
    }

    [Fact]
    public async Task Denied_pairing_stores_nothing_and_reports_the_reason()
    {
        _approver.Allow = false;
        StartServer();
        using var client = Client();
        var ex = await Assert.ThrowsAsync<AgentPairingException>(() => client.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(AgentErrorCode.PairingDenied, ex.Code);
        Assert.False(ex.NeedsPairing);
        Assert.Empty(_viewers);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Unanswered_dialog_counts_as_denied()
    {
        _approver.Delay = TimeSpan.FromSeconds(30);
        StartServer(pairingTimeout: TimeSpan.FromMilliseconds(300));
        using var client = Client();
        var ex = await Assert.ThrowsAsync<AgentPairingException>(() => client.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(AgentErrorCode.PairingDenied, ex.Code);
        Assert.Empty(_viewers);
    }

    [Fact]
    public async Task Pairing_disabled_refuses_pairing_but_the_token_still_works()
    {
        StartServer(token: Token, allowPairing: false);
        using (var pairing = Client())
        {
            var ex = await Assert.ThrowsAsync<AgentPairingException>(() => pairing.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(AgentErrorCode.PairingDisabled, ex.Code);
        }
        Assert.Empty(_approver.Requests);
        using var tokenClient = Client(token: Token);
        await tokenClient.ConnectAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tokenClient.IsConnected);
    }

    [Fact]
    public async Task Unknown_viewer_with_a_stale_key_is_told_to_pair_again()
    {
        StartServer();
        using var client = Client(pairKey: Convert.ToBase64String(new byte[32]));
        var ex = await Assert.ThrowsAsync<AgentPairingException>(() => client.ConnectAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(ex.NeedsPairing);
    }

    [Fact]
    public async Task Removed_viewer_is_told_to_pair_again_and_can()
    {
        StartServer();
        string key;
        using (var first = Client()) { await first.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); key = first.PairedKey!; }
        _viewers.Clear(); // "Remove" in the agent's settings

        using (var stale = Client(pairKey: key))
        {
            var ex = await Assert.ThrowsAsync<AgentPairingException>(() => stale.ConnectAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(ex.NeedsPairing);
        }
        using var again = Client();
        await again.PairAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(again.IsConnected);
        Assert.NotEqual(key, again.PairedKey); // fresh ECDH exchange, fresh key
        Assert.Equal(2, _approver.Requests.Count);
    }

    [Fact]
    public async Task Wrong_token_from_a_viewer_with_identity_is_not_mistaken_for_pairing()
    {
        StartServer(token: Token);
        using var client = Client(token: "wrong");
        var ex = await Assert.ThrowsAsync<AgentPairingException>(() => client.ConnectAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(ex.NeedsPairing); // unknown viewer id and bad token: the viewer may pair instead
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Server_refuses_a_configuration_in_which_nobody_could_connect()
    {
        Assert.Throws<ArgumentException>(() => new AgentServer(_clipboard, new AgentServerOptions { AllowPairing = false, ListenAddress = IPAddress.Loopback, Port = 0 }));
    }

    [Fact]
    public void Ecdh_both_sides_agree_and_a_third_party_does_not()
    {
        using var a = AgentPairing.NewKeyPair();
        using var b = AgentPairing.NewKeyPair();
        using var eve = AgentPairing.NewKeyPair();
        var ab = AgentPairing.DeriveKey(a, AgentPairing.ExportPublicKey(b));
        var ba = AgentPairing.DeriveKey(b, AgentPairing.ExportPublicKey(a));
        Assert.Equal(ab, ba);
        Assert.Equal(32, Convert.FromBase64String(ab).Length);
        Assert.NotEqual(ab, AgentPairing.DeriveKey(eve, AgentPairing.ExportPublicKey(a)));
        Assert.Throws<AgentProtocolException>(() => AgentPairing.DeriveKey(a, "not a key"));
    }
}
