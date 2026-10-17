using System.Net;
using System.Text;
using BetterVnc.Agent.Protocol;
using Xunit;

namespace BetterVnc.Agent.Tests;

public class FramingTests
{
    [Fact]
    public async Task Frame_round_trips_header_and_payload()
    {
        var header = new AgentMessage
        {
            Type = AgentMessageType.ClipboardData,
            Id = 7,
            Format = AgentFormat.Files,
            Files = new() { new AgentFileEntry { Name = "a/b.txt", Size = 3 } },
        };
        var payload = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, header, payload, default);
        ms.Position = 0;

        var frame = await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, default);
        Assert.NotNull(frame);
        Assert.Equal(AgentMessageType.ClipboardData, frame!.Header.Type);
        Assert.Equal(7, frame.Header.Id);
        Assert.Equal(AgentFormat.Files, frame.Header.Format);
        Assert.Equal("a/b.txt", frame.Header.Files![0].Name);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.Null(await AgentFraming.ReadAsync(ms, AgentFraming.DefaultMaxPayloadBytes, default)); // clean EOF
    }

    [Fact]
    public async Task Header_omits_nulls_and_uses_camel_case()
    {
        var json = Encoding.UTF8.GetString(AgentFraming.EncodeHeader(AgentMessage.Of(AgentMessageType.Ping, 3)));
        Assert.Equal("{\"type\":\"ping\",\"id\":3}", json);
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, AgentMessage.Of(AgentMessageType.Ping), default, default);
        Assert.Equal(8 + "{\"type\":\"ping\"}".Length, ms.Length);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_before_allocation()
    {
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, AgentMessage.Of(AgentMessageType.SetClipboard), new byte[2000], default);
        ms.Position = 0;
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await AgentFraming.ReadAsync(ms, maxPayloadBytes: 1000, default));
    }

    [Fact]
    public async Task Truncated_frame_is_an_error_not_eof()
    {
        using var ms = new MemoryStream();
        await AgentFraming.WriteAsync(ms, AgentMessage.Of(AgentMessageType.Ping), new byte[10], default);
        var truncated = new MemoryStream(ms.ToArray(), 0, (int)ms.Length - 4);
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await AgentFraming.ReadAsync(truncated, AgentFraming.DefaultMaxPayloadBytes, default));
    }

    [Fact]
    public void Proof_verifies_only_with_the_same_token_and_nonce()
    {
        var nonce = AgentAuth.NewNonce();
        var proof = AgentAuth.Prove("secret", nonce);
        Assert.True(AgentAuth.Verify("secret", nonce, proof));
        Assert.True(AgentAuth.Verify(" secret \n", nonce, proof)); // whitespace from copy/paste is forgiven
        Assert.False(AgentAuth.Verify("other", nonce, proof));
        Assert.False(AgentAuth.Verify("secret", AgentAuth.NewNonce(), proof));
        Assert.False(AgentAuth.Verify("secret", nonce, "not base64!"));
        Assert.False(AgentAuth.Verify("secret", nonce, null));
    }
}

public class FileBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bettervnc-tests", Guid.NewGuid().ToString("N"));

    public FileBundleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Pack_and_unpack_preserve_files_and_directory_layout()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "folder", "nested"));
        File.WriteAllText(Path.Combine(src, "one.txt"), "one");
        File.WriteAllBytes(Path.Combine(src, "folder", "two.bin"), new byte[] { 0, 255, 7 });
        File.WriteAllText(Path.Combine(src, "folder", "nested", "three.txt"), "three");

        var (entries, payload) = FileBundle.Pack(new[] { Path.Combine(src, "one.txt"), Path.Combine(src, "folder") });
        Assert.Equal(3, entries.Count);
        Assert.Equal("one.txt", entries[0].Name);
        Assert.Contains(entries, e => e.Name == "folder/two.bin" && e.Size == 3);
        Assert.Contains(entries, e => e.Name == "folder/nested/three.txt" && e.Size == 5);
        Assert.Equal(3 + 3 + 5, payload.Length);

        var dest = Path.Combine(_dir, "dest");
        var top = FileBundle.Unpack(entries, payload, dest);
        Assert.Equal(new[] { Path.Combine(dest, "one.txt"), Path.Combine(dest, "folder") }, top);
        Assert.Equal("one", File.ReadAllText(Path.Combine(dest, "one.txt")));
        Assert.Equal(new byte[] { 0, 255, 7 }, File.ReadAllBytes(Path.Combine(dest, "folder", "two.bin")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(dest, "folder", "nested", "three.txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void Unpack_rejects_names_that_escape_the_target(string name)
    {
        var entries = new List<AgentFileEntry> { new() { Name = name, Size = 1 } };
        Assert.Throws<AgentProtocolException>(() => FileBundle.Unpack(entries, new byte[] { 1 }, Path.Combine(_dir, "dest")));
    }

    [Fact]
    public void Unpack_rejects_bundle_shorter_than_declared()
    {
        var entries = new List<AgentFileEntry> { new() { Name = "x.bin", Size = 10 } };
        Assert.Throws<AgentProtocolException>(() => FileBundle.Unpack(entries, new byte[5], Path.Combine(_dir, "dest")));
    }

    [Fact]
    public void Pack_enforces_the_size_limit()
    {
        var f = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(f, new byte[100]);
        Assert.Throws<InvalidOperationException>(() => FileBundle.Pack(new[] { f }, maxBytes: 50));
    }
}

public class ServerClientTests : IAsyncDisposable
{
    private const string Token = "test-token-123";
    private readonly FakeClipboard _clipboard = new();
    private readonly AgentServer _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bettervnc-tests", Guid.NewGuid().ToString("N"));

    public ServerClientTests()
    {
        Directory.CreateDirectory(_dir);
        _server = new AgentServer(_clipboard, new AgentServerOptions
        {
            Token = Token,
            ListenAddress = IPAddress.Loopback,
            Port = 0,
            StagingDirectory = Path.Combine(_dir, "staging"),
            MaxPayloadBytes = 1024 * 1024,
        });
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentConnection NewClient(string token = Token) =>
        new(new AgentConnectionOptions { Host = "127.0.0.1", Port = _server.Port, Token = token, AppName = "tests", MaxPayloadBytes = 1024 * 1024 });

    private static Task<T> Within<T>(Task<T> t) => t.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Handshake_succeeds_and_reports_formats()
    {
        _clipboard.Put(AgentFormat.Text, Encoding.UTF8.GetBytes("hi"));
        using var client = NewClient();
        await client.ConnectAsync(default);
        Assert.True(client.IsConnected);
        Assert.Equal("fake", client.RemoteOs);
        Assert.Equal(AgentMessage.ProtocolVersion, client.RemoteVersion);
        Assert.Equal(new[] { AgentFormat.Text }, client.RemoteFormats);
        var rtt = await Within(client.PingAsync(default));
        Assert.True(rtt < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wrong_token_is_rejected_with_authentication_error()
    {
        using var client = NewClient("wrong");
        var ex = await Assert.ThrowsAsync<AgentAuthenticationException>(() => client.ConnectAsync(default));
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(client.IsConnected);
        // The server must not have accepted a session and must not have touched the clipboard.
        await Task.Delay(100);
        Assert.Equal(0, _server.SessionCount);
        Assert.Equal(0, _clipboard.SetDataCalls);
    }

    [Fact]
    public async Task Client_detects_a_server_that_cannot_prove_the_token()
    {
        // A fake "agent" that sends a hello and a welcome with a bogus proof.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var s = tcp.GetStream();
            await AgentFraming.WriteAsync(s, new AgentMessage { Type = AgentMessageType.Hello, Version = 1, Os = "evil", Nonce = AgentAuth.NewNonce() }, default, default);
            var hello = await AgentFraming.ReadAsync(s, 65536, default);
            await AgentFraming.WriteAsync(s, new AgentMessage { Type = AgentMessageType.Welcome, Version = 1, Os = "evil", Proof = AgentAuth.Prove("guess", hello!.Header.Nonce!) }, default, default);
        });
        using var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Token = Token });
        await Assert.ThrowsAsync<AgentAuthenticationException>(() => client.ConnectAsync(default));
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    [Fact]
    public async Task Get_clipboard_returns_png_bytes_and_null_when_absent()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 };
        _clipboard.Put(AgentFormat.Png, png);
        using var client = NewClient();
        await client.ConnectAsync(default);

        var frame = await Within(client.GetClipboardAsync(AgentFormat.Png, default));
        Assert.NotNull(frame);
        Assert.Equal(AgentFormat.Png, frame!.Header.Format);
        Assert.Equal(png, frame.Payload.ToArray());

        Assert.Null(await Within(client.GetClipboardAsync(AgentFormat.Text, default)));
        await Assert.ThrowsAsync<AgentProtocolException>(() => Within(client.GetClipboardAsync("bmp", default)));
    }

    [Fact]
    public async Task Get_clipboard_files_bundles_guest_files()
    {
        var guestDir = Path.Combine(_dir, "guest");
        Directory.CreateDirectory(Path.Combine(guestDir, "photos"));
        File.WriteAllText(Path.Combine(guestDir, "notes.txt"), "notes");
        File.WriteAllBytes(Path.Combine(guestDir, "photos", "a.jpg"), new byte[] { 9, 9 });
        _clipboard.PutFiles(Path.Combine(guestDir, "notes.txt"), Path.Combine(guestDir, "photos"));

        using var client = NewClient();
        await client.ConnectAsync(default);
        var frame = await Within(client.GetClipboardAsync(AgentFormat.Files, default));
        Assert.NotNull(frame);
        Assert.Equal(2, frame!.Header.Files!.Count);
        var outDir = Path.Combine(_dir, "received");
        var top = FileBundle.Unpack(frame.Header.Files, frame.Payload, outDir);
        Assert.Equal("notes", File.ReadAllText(Path.Combine(outDir, "notes.txt")));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(Path.Combine(outDir, "photos", "a.jpg")));
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public async Task Set_clipboard_png_lands_in_guest_clipboard_without_echo()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var changes = 0;
        client.ClipboardChanged += _ => Interlocked.Increment(ref changes);

        var png = new byte[] { 0x89, 1, 2, 3 };
        await Within(client.SetClipboardAsync(AgentFormat.Png, png, default).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
        Assert.Equal(AgentFormat.Png, _clipboard.LastSetFormat);
        Assert.Equal(png, _clipboard.LastSetData);

        await Task.Delay(200); // several poll intervals
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Set_clipboard_files_writes_them_to_staging_and_sets_paths()
    {
        var local = Path.Combine(_dir, "local");
        Directory.CreateDirectory(Path.Combine(local, "dir"));
        File.WriteAllText(Path.Combine(local, "dir", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(local, "top.txt"), "top");

        using var client = NewClient();
        await client.ConnectAsync(default);
        await client.SetClipboardFilesAsync(new[] { Path.Combine(local, "top.txt"), Path.Combine(local, "dir") }, default).WaitAsync(TimeSpan.FromSeconds(5));

        var set = _clipboard.LastSetFiles;
        Assert.NotNull(set);
        Assert.Equal(2, set!.Count);
        Assert.All(set, p => Assert.StartsWith(Path.Combine(_dir, "staging"), p));
        Assert.Equal("top", File.ReadAllText(set[0]));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(set[1], "inner.txt")));
    }

    [Fact]
    public async Task Guest_clipboard_change_is_pushed_to_connected_viewer()
    {
        using var client = NewClient();
        await client.ConnectAsync(default);
        var tcs = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ClipboardChanged += m => tcs.TrySetResult(m);

        var f = Path.Combine(_dir, "copied.txt");
        File.WriteAllText(f, "hello world");
        _clipboard.PutFiles(f);

        var msg = await Within(tcs.Task);
        Assert.Equal(new[] { AgentFormat.Files }, msg.Formats);
        Assert.Single(msg.Files!);
        Assert.Equal("copied.txt", msg.Files![0].Name);
        Assert.Equal(11, msg.Files[0].Size);
        Assert.Equal(11, msg.TotalFileBytes);
    }

    [Fact]
    public async Task Clipboard_content_present_before_connect_is_not_announced()
    {
        _clipboard.Put(AgentFormat.Text, Encoding.UTF8.GetBytes("old"));
        await Task.Delay(100);
        using var client = NewClient();
        await client.ConnectAsync(default);
        var changes = 0;
        client.ClipboardChanged += _ => Interlocked.Increment(ref changes);
        await Task.Delay(150);
        Assert.Equal(0, changes);
        Assert.Equal(new[] { AgentFormat.Text }, client.RemoteFormats); // but the welcome still describes it
    }

    [Fact]
    public async Task Server_close_raises_disconnected_and_fails_pending_requests()
    {
        var clipboard = new FakeClipboard();
        var server = new AgentServer(clipboard, new AgentServerOptions { Token = Token, ListenAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        using var client = new AgentConnection(new AgentConnectionOptions { Host = "127.0.0.1", Port = server.Port, Token = Token });
        await client.ConnectAsync(default);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex => disconnected.TrySetResult(ex);

        await server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => client.PingAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Garbage_on_the_wire_is_rejected_cleanly()
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        var s = tcp.GetStream();
        var hello = await AgentFraming.ReadAsync(s, 65536, default);
        Assert.Equal(AgentMessageType.Hello, hello!.Header.Type);
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        // Either an error frame or a closed connection is acceptable; the server must not hang or crash.
        try
        {
            var reply = await AgentFraming.ReadAsync(s, 65536, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (reply != null) Assert.Equal(AgentMessageType.Error, reply.Header.Type);
        }
        catch (AgentProtocolException) { }
        catch (IOException) { }
        await Task.Delay(100);
        Assert.Equal(0, _server.SessionCount);
    }
}
