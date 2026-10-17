using System.Buffers.Binary;
using System.Text;
using BetterVnc.Protocol;
using Xunit;

namespace BetterVnc.Protocol.Tests;

public class ClientTests
{
    private static async Task<(FakeRfbServer server, VncClient client)> ConnectAsync(Action<FakeRfbServer>? configure = null, Action<VncConnectionOptions>? options = null)
    {
        var server = new FakeRfbServer();
        configure?.Invoke(server);
        var opts = new VncConnectionOptions { Host = "127.0.0.1", Port = server.Port, EnableQemuAudio = true };
        options?.Invoke(opts);
        var client = new VncClient(opts);
        await client.ConnectAsync();
        await server.Initialised;
        // The client sends SetPixelFormat, SetEncodings and a full update request right after init.
        server.WaitFor(ClientMessage.SetPixelFormat);
        server.WaitFor(ClientMessage.SetEncodings);
        server.WaitFor(ClientMessage.FramebufferUpdateRequest);
        return (server, client);
    }

    private static uint Pixel(VncClient c, int x, int y)
    {
        var fb = c.Framebuffer;
        return BinaryPrimitives.ReadUInt32LittleEndian(fb.Pixels.AsSpan(fb.Offset(x, y), 4)) & 0x00FFFFFF;
    }

    private static byte[] Bgrx(uint rgb, int count)
    {
        var data = new byte[count * 4];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), rgb);
        return data;
    }

    private static Task<RfbRect[]> NextUpdate(VncClient client)
    {
        var tcs = new TaskCompletionSource<RfbRect[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RfbRect[] r) { client.FramebufferUpdated -= Handler; tcs.TrySetResult(r); }
        client.FramebufferUpdated += Handler;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Handshake_none_auth_initialises_framebuffer()
    {
        var (server, client) = await ConnectAsync(s => { s.Width = 100; s.Height = 70; s.Name = "Ünïcode VM"; });
        using (server) using (client)
        {
            Assert.Equal(100, client.Framebuffer.Width);
            Assert.Equal(70, client.Framebuffer.Height);
            Assert.Equal("Ünïcode VM", client.DesktopName);
            Assert.Equal(PixelFormat.Bgrx32, server.ClientPixelFormat);
            Assert.Contains(RfbEncoding.Tight, server.ClientEncodings);
            Assert.Contains(RfbEncoding.ExtendedClipboard, server.ClientEncodings);
            Assert.Contains(RfbEncoding.QemuExtendedKeyEvent, server.ClientEncodings);
        }
    }

    [Fact]
    public async Task Vnc_password_auth_succeeds()
    {
        var (server, client) = await ConnectAsync(
            s => { s.SecurityTypes = new[] { SecurityType.VncAuth }; s.VncPassword = "s3cretpassword-long"; },
            o => o.Password = "s3cretpassword-long");
        using (server) using (client)
            Assert.Equal(SecurityType.VncAuth, client.Capabilities.SecurityTypeUsed);
    }

    [Fact]
    public async Task Vnc_password_auth_failure_raises_auth_exception()
    {
        using var server = new FakeRfbServer { SecurityTypes = new[] { SecurityType.VncAuth }, VncPassword = "right" };
        using var client = new VncClient(new VncConnectionOptions { Host = "127.0.0.1", Port = server.Port, Password = "wrong" });
        await Assert.ThrowsAsync<VncAuthenticationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Apple_ard_auth_succeeds_against_3_889_server()
    {
        var (server, client) = await ConnectAsync(
            s =>
            {
                s.Version = "RFB 003.889\n";
                s.SecurityTypes = new[] { SecurityType.AppleArd, SecurityType.Apple33, SecurityType.Apple36 };
                s.AppleCredentials = ("tim", "pässwörd");
            },
            o => { o.Username = "tim"; o.Password = "pässwörd"; });
        using (server) using (client)
        {
            Assert.True(client.Capabilities.IsApple);
            Assert.Equal(SecurityType.AppleArd, client.Capabilities.SecurityTypeUsed);
        }
    }

    [Fact]
    public async Task Credential_provider_is_consulted_when_no_password_given()
    {
        var kinds = new List<CredentialKind>();
        var (server, client) = await ConnectAsync(
            s => { s.SecurityTypes = new[] { SecurityType.VncAuth }; s.VncPassword = "pw"; },
            o => o.CredentialProvider = (kind, _) => { kinds.Add(kind); return new VncCredentials(null, "pw"); });
        using (server) using (client)
            Assert.Equal(new[] { CredentialKind.Password }, kinds);
    }

    [Fact]
    public async Task Raw_and_copyrect_decode()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(2, 3, 4, 2, RfbEncoding.Raw, Bgrx(0x123456, 8)));
            var dirty = await upd;
            // The initial allocation marks the whole screen dirty, then the decoded rect follows.
            Assert.Equal(new RfbRect(2, 3, 4, 2), dirty[^1]);
            Assert.Equal(0x123456u, Pixel(client, 2, 3));
            Assert.Equal(0x123456u, Pixel(client, 5, 4));
            Assert.Equal(0u, Pixel(client, 6, 4));

            upd = NextUpdate(client);
            var copy = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(copy, 2);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), 3);
            server.SendUpdate(new FakeRfbServer.Rect(10, 10, 4, 2, RfbEncoding.CopyRect, copy));
            await upd;
            Assert.Equal(0x123456u, Pixel(client, 13, 11));
            Assert.Equal(1u, (uint)client.Statistics.CopyRects);
            // The client must keep the incremental request loop alive.
            server.WaitFor(ClientMessage.FramebufferUpdateRequest, 2000, m => m.Payload[0] == 1);
        }
    }

    [Fact]
    public async Task Rre_decodes_background_and_subrects()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var b = new RfbMessageBuilder();
            b.U32(1).Bytes(Bgrx(0xFF0000, 1)).Bytes(Bgrx(0x00FF00, 1)).U16(1).U16(1).U16(2).U16(2);
            var upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 8, 8, RfbEncoding.Rre, b.Data.ToArray()));
            await upd;
            Assert.Equal(0xFF0000u, Pixel(client, 0, 0));
            Assert.Equal(0x00FF00u, Pixel(client, 1, 1));
            Assert.Equal(0x00FF00u, Pixel(client, 2, 2));
            Assert.Equal(0xFF0000u, Pixel(client, 3, 3));
        }
    }

    [Fact]
    public async Task Hextile_decodes_raw_and_subrect_tiles()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            // Rect 32x16 = two tiles. Tile 1: raw. Tile 2: bg + fg + 1 subrect using fg, then 1 coloured subrect.
            var b = new RfbMessageBuilder();
            b.U8(1).Bytes(Bgrx(0x111111, 256));
            b.U8(2 | 4 | 8).Bytes(Bgrx(0x222222, 1)).Bytes(Bgrx(0x333333, 1)).U8(1).U8(0x21).U8(0x10); // x=2,y=1,w=2,h=1
            var upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 32, 16, RfbEncoding.Hextile, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x111111u, Pixel(client, 15, 15));
            Assert.Equal(0x222222u, Pixel(client, 16, 0));
            Assert.Equal(0x333333u, Pixel(client, 18, 1));
            Assert.Equal(0x333333u, Pixel(client, 19, 1));
            Assert.Equal(0x222222u, Pixel(client, 20, 1));
        }
    }

    [Fact]
    public async Task Zlib_encoding_keeps_stream_across_rects()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var enc = new FakeRfbServer.ZlibStreamEncoder();
            for (int i = 0; i < 3; i++)
            {
                uint colour = 0x010101u * (uint)(i + 1) * 40;
                var compressed = enc.Compress(Bgrx(colour, 16));
                var b = new RfbMessageBuilder();
                b.U32((uint)compressed.Length).Bytes(compressed);
                var upd = NextUpdate(client);
                server.SendUpdate(new FakeRfbServer.Rect(i * 4, 0, 4, 4, RfbEncoding.Zlib, b.Data.ToArray()));
                await upd;
                Assert.Equal(colour, Pixel(client, i * 4 + 3, 3));
            }
        }
    }

    [Fact]
    public async Task Tight_fill_copy_palette_and_gradient()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            // Fill
            var upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 8, 8, RfbEncoding.Tight, new byte[] { 0x80, 0x12, 0x34, 0x56 }));
            await upd;
            Assert.Equal(0x123456u, Pixel(client, 7, 7));

            // Basic copy filter, stream 0, compressed (16x4 = 192 bytes > 12), with the stream reset bit.
            var enc0 = new FakeRfbServer.ZlibStreamEncoder();
            var tpix = new byte[16 * 4 * 3];
            for (int i = 0; i < 64; i++) { tpix[i * 3] = 0xAA; tpix[i * 3 + 1] = 0xBB; tpix[i * 3 + 2] = (byte)i; }
            var z = enc0.Compress(tpix);
            var b = new RfbMessageBuilder();
            b.U8(0x01).Bytes(CompactLength(z.Length)).Bytes(z);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 8, 16, 4, RfbEncoding.Tight, b.Data.ToArray()));
            await upd;
            Assert.Equal(0xAABB00u, Pixel(client, 0, 8));
            Assert.Equal(0xAABB3Fu, Pixel(client, 15, 11));

            // Second rect on the same stream without reset: proves stream continuity.
            var tpix2 = new byte[16 * 4 * 3];
            for (int i = 0; i < 64; i++) { tpix2[i * 3] = 0x01; tpix2[i * 3 + 1] = 0x02; tpix2[i * 3 + 2] = 0x03; }
            z = enc0.Compress(tpix2);
            b = new RfbMessageBuilder();
            b.U8(0x00).Bytes(CompactLength(z.Length)).Bytes(z);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(16, 8, 16, 4, RfbEncoding.Tight, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x010203u, Pixel(client, 31, 11));

            // Palette filter with 2 colours, uncompressed (8x1 = 1 byte < 12), explicit filter on stream 1.
            b = new RfbMessageBuilder();
            b.U8(0x40 | 0x10).U8(1).U8(1).Bytes(new byte[] { 0, 0, 0, 0xFF, 0xFF, 0xFF }).U8(0b10100000);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 20, 8, 1, RfbEncoding.Tight, b.Data.ToArray()));
            await upd;
            Assert.Equal(0xFFFFFFu, Pixel(client, 0, 20));
            Assert.Equal(0x000000u, Pixel(client, 1, 20));
            Assert.Equal(0xFFFFFFu, Pixel(client, 2, 20));
            Assert.Equal(0x000000u, Pixel(client, 7, 20));

            // Gradient filter: encode a horizontal ramp (2x2) uncompressed: 12 bytes -> compressed path threshold is < 12, so 12 is compressed.
            // Use 3x1 (9 bytes) to stay uncompressed. Pixels: (10,20,30), (20,20,30), (30,20,30)
            // Residuals: first = value (pred 0); second = (20-10, 0, 0); third = (10,0,0)
            b = new RfbMessageBuilder();
            b.U8(0x40 | 0x20).U8(2).Bytes(new byte[] { 10, 20, 30, 10, 0, 0, 10, 0, 0 });
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 30, 3, 1, RfbEncoding.Tight, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x0A141Eu, Pixel(client, 0, 30));
            Assert.Equal(0x14141Eu, Pixel(client, 1, 30));
            Assert.Equal(0x1E141Eu, Pixel(client, 2, 30));
        }
    }

    private static byte[] CompactLength(int len)
    {
        var list = new List<byte>();
        if (len < 128) list.Add((byte)len);
        else if (len < 16384) { list.Add((byte)(len & 0x7F | 0x80)); list.Add((byte)(len >> 7)); }
        else { list.Add((byte)(len & 0x7F | 0x80)); list.Add((byte)((len >> 7) & 0x7F | 0x80)); list.Add((byte)(len >> 14)); }
        return list.ToArray();
    }

    [Fact]
    public async Task Zrle_decodes_all_tile_types()
    {
        var (server, client) = await ConnectAsync(s => { s.Width = 128; s.Height = 64; });
        using (server) using (client)
        {
            // Rect 128x64 = two 64x64 tiles... make it 5 tiles wide by using 320x? Framebuffer is 128 wide, so use two rects.
            var enc = new FakeRfbServer.ZlibStreamEncoder();

            // Rect A (0,0,128,64): tile 1 raw, tile 2 solid.
            var tiles = new RfbMessageBuilder();
            tiles.U8(0);
            for (int i = 0; i < 64 * 64; i++) tiles.U8(0x11).U8(0x22).U8(0x33); // B,G,R
            tiles.U8(1).U8(0x44).U8(0x55).U8(0x66);
            var z = enc.Compress(tiles.Data.ToArray());
            var b = new RfbMessageBuilder();
            b.U32((uint)z.Length).Bytes(z);
            var upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 128, 64, RfbEncoding.Zrle, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x332211u, Pixel(client, 63, 63));
            Assert.Equal(0x665544u, Pixel(client, 64, 0));
            Assert.Equal(0x665544u, Pixel(client, 127, 63));

            // Rect B (0,0,8,2): packed palette with 2 colours, 1 bit per pixel.
            tiles = new RfbMessageBuilder();
            tiles.U8(2).Bytes(new byte[] { 0, 0, 0, 0xFF, 0xFF, 0xFF }).U8(0b11000000).U8(0b00000011);
            z = enc.Compress(tiles.Data.ToArray());
            b = new RfbMessageBuilder();
            b.U32((uint)z.Length).Bytes(z);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 8, 2, RfbEncoding.Zrle, b.Data.ToArray()));
            await upd;
            Assert.Equal(0xFFFFFFu, Pixel(client, 0, 0));
            Assert.Equal(0xFFFFFFu, Pixel(client, 1, 0));
            Assert.Equal(0x000000u, Pixel(client, 2, 0));
            Assert.Equal(0xFFFFFFu, Pixel(client, 7, 1));
            Assert.Equal(0x000000u, Pixel(client, 5, 1));

            // Rect C (10,10,4,2): plain RLE: 5 pixels of A, then 3 of B.
            tiles = new RfbMessageBuilder();
            tiles.U8(128).Bytes(new byte[] { 1, 2, 3 }).U8(4).Bytes(new byte[] { 4, 5, 6 }).U8(2);
            z = enc.Compress(tiles.Data.ToArray());
            b = new RfbMessageBuilder();
            b.U32((uint)z.Length).Bytes(z);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(10, 10, 4, 2, RfbEncoding.Zrle, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x030201u, Pixel(client, 10, 10));
            Assert.Equal(0x030201u, Pixel(client, 10, 11));
            Assert.Equal(0x060504u, Pixel(client, 11, 11));
            Assert.Equal(0x060504u, Pixel(client, 13, 11));

            // Rect D (20,20,4,1): palette RLE with 2 colours: idx0 run 3 (0x80|0, len byte 2), then idx1 single.
            tiles = new RfbMessageBuilder();
            tiles.U8(130).Bytes(new byte[] { 9, 9, 9, 7, 7, 7 }).U8(0x80).U8(2).U8(1);
            z = enc.Compress(tiles.Data.ToArray());
            b = new RfbMessageBuilder();
            b.U32((uint)z.Length).Bytes(z);
            upd = NextUpdate(client);
            server.SendUpdate(new FakeRfbServer.Rect(20, 20, 4, 1, RfbEncoding.Zrle, b.Data.ToArray()));
            await upd;
            Assert.Equal(0x090909u, Pixel(client, 22, 20));
            Assert.Equal(0x070707u, Pixel(client, 23, 20));
        }
    }

    [Fact]
    public async Task Cursor_pseudo_encoding_raises_cursor_changed()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var tcs = new TaskCompletionSource<CursorShape>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.CursorChanged += c => tcs.TrySetResult(c);
            var data = Bgrx(0xFF00FF, 4).Concat(new byte[] { 0b10000000, 0b01000000 }).ToArray(); // 2x2, mask: (0,0) and (1,1) opaque
            server.SendUpdate(new FakeRfbServer.Rect(1, 1, 2, 2, RfbEncoding.Cursor, data));
            var shape = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, shape.Width);
            Assert.Equal(1, shape.HotX);
            Assert.Equal(255, shape.Bgra[3]);
            Assert.Equal(0, shape.Bgra[7]);
            Assert.Equal(255, shape.Bgra[15]);
            Assert.True(client.Capabilities.LocalCursor);
        }
    }

    [Fact]
    public async Task Desktop_size_resizes_framebuffer_and_requests_full_update()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var tcs = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.FramebufferResized += (w, h) => tcs.TrySetResult((w, h));
            server.SendUpdate(new FakeRfbServer.Rect(0, 0, 200, 150, RfbEncoding.DesktopSize, Array.Empty<byte>()));
            var size = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((200, 150), size);
            Assert.Equal(200 * 150 * 4, client.Framebuffer.Pixels.Length);
            var req = server.WaitFor(ClientMessage.FramebufferUpdateRequest, 2000, m => m.Payload[0] == 0);
            Assert.Equal(200, BinaryPrimitives.ReadUInt16BigEndian(req.Payload.AsSpan(5)));
        }
    }

    [Fact]
    public async Task Legacy_server_cut_text_is_delivered()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ClipboardTextReceived += t => tcs.TrySetResult(t);
            server.SendServerCutText("héllo\r\nworld");
            Assert.Equal("héllo\nworld", await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            client.SendClipboardText("from client");
            var msg = server.WaitFor(ClientMessage.ClientCutText);
            Assert.Equal(11, BinaryPrimitives.ReadInt32BigEndian(msg.Payload));
            Assert.Equal("from client", Encoding.Latin1.GetString(msg.Payload, 4, 11));
        }
    }

    [Fact]
    public async Task Extended_clipboard_full_round_trip()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            // Server announces caps -> client answers with its caps.
            var caps = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(caps, 1 << 20);
            server.SendExtendedClipboard(ClipboardFlags.Caps | ClipboardFlags.Utf8Text, caps);
            var clientCaps = server.WaitFor(ClientMessage.ClientCutText);
            Assert.True(BinaryPrimitives.ReadInt32BigEndian(clientCaps.Payload) < 0);
            Assert.Equal(ClipboardFlags.Caps | ClipboardFlags.Utf8Text, BinaryPrimitives.ReadUInt32BigEndian(clientCaps.Payload.AsSpan(4)));
            Assert.True(client.Capabilities.ExtendedClipboard);

            // Server provides text -> client event.
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ClipboardTextReceived += t => tcs.TrySetResult(t);
            var body = Encoding.UTF8.GetBytes("日本語 text\r\nline2\0");
            var inner = new RfbMessageBuilder();
            inner.U32((uint)body.Length).Bytes(body);
            server.SendExtendedClipboard(ClipboardFlags.Provide | ClipboardFlags.Utf8Text, FakeRfbServer.ZlibFinish(inner.Data.ToArray()));
            Assert.Equal("日本語 text\nline2", await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // Server notifies -> client requests.
            server.SendExtendedClipboard(ClipboardFlags.Notify | ClipboardFlags.Utf8Text);
            var req = server.WaitFor(ClientMessage.ClientCutText);
            Assert.Equal(ClipboardFlags.Request | ClipboardFlags.Utf8Text, BinaryPrimitives.ReadUInt32BigEndian(req.Payload.AsSpan(4)));

            // Client has new local text -> notify; server requests -> provide.
            client.SendClipboardText("local ✓");
            var notify = server.WaitFor(ClientMessage.ClientCutText);
            Assert.Equal(ClipboardFlags.Notify | ClipboardFlags.Utf8Text, BinaryPrimitives.ReadUInt32BigEndian(notify.Payload.AsSpan(4)));
            server.SendExtendedClipboard(ClipboardFlags.Request | ClipboardFlags.Utf8Text);
            var provide = server.WaitFor(ClientMessage.ClientCutText);
            Assert.Equal(ClipboardFlags.Provide | ClipboardFlags.Utf8Text, BinaryPrimitives.ReadUInt32BigEndian(provide.Payload.AsSpan(4)));
            var inflated = new Decoders.ZlibInflater().InflateAll(provide.Payload, 8, provide.Payload.Length - 8);
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(inflated);
            Assert.Equal("local ✓\0", Encoding.UTF8.GetString(inflated, 4, len));
        }
    }

    [Fact]
    public async Task Key_events_use_qemu_extension_once_acknowledged()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            client.SendKeyEvent('a', true, 0x1E);
            var plain = server.WaitFor(ClientMessage.KeyEvent);
            Assert.Equal(1, plain.Payload[0]);
            Assert.Equal('a', (int)BinaryPrimitives.ReadUInt32BigEndian(plain.Payload.AsSpan(3)));

            var upd = NextUpdate(client);
            server.SendUpdate(
                new FakeRfbServer.Rect(0, 0, 64, 48, RfbEncoding.QemuExtendedKeyEvent, Array.Empty<byte>()),
                new FakeRfbServer.Rect(0, 0, 1, 1, RfbEncoding.Raw, Bgrx(1, 1)));
            await upd;
            Assert.True(client.Capabilities.QemuExtendedKeyEvents);

            client.SendKeyEvent(Keysym.ControlR, true, 0xE01D);
            var ext = server.WaitFor(ClientMessage.Qemu);
            Assert.Equal(QemuMessage.ExtendedKeyEvent, ext.Payload[0]);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(ext.Payload.AsSpan(1)));
            Assert.Equal(Keysym.ControlR, BinaryPrimitives.ReadUInt32BigEndian(ext.Payload.AsSpan(3)));
            Assert.Equal(0x9Du, BinaryPrimitives.ReadUInt32BigEndian(ext.Payload.AsSpan(7)));
        }
    }

    [Fact]
    public async Task Pointer_events_are_clamped_and_encoded()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            client.SendPointerEvent(1000, -5, PointerButton.Left | PointerButton.Right);
            var msg = server.WaitFor(ClientMessage.PointerEvent);
            Assert.Equal(PointerButton.Left | PointerButton.Right, msg.Payload[0]);
            Assert.Equal(63, BinaryPrimitives.ReadUInt16BigEndian(msg.Payload.AsSpan(1)));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(msg.Payload.AsSpan(3)));
        }
    }

    [Fact]
    public async Task Audio_messages_are_delivered()
    {
        var (server, client) = await ConnectAsync();
        using (server) using (client)
        {
            var upd = NextUpdate(client);
            server.SendUpdate(
                new FakeRfbServer.Rect(0, 0, 64, 48, RfbEncoding.QemuAudio, Array.Empty<byte>()),
                new FakeRfbServer.Rect(0, 0, 1, 1, RfbEncoding.Raw, Bgrx(1, 1)));
            await upd;
            Assert.True(client.Capabilities.QemuAudio);

            client.SetAudioEnabled(true);
            var fmt = server.WaitFor(ClientMessage.Qemu, 5000, m => m.Payload[0] == QemuMessage.Audio);
            Assert.Equal(QemuMessage.AudioSetFormat, BinaryPrimitives.ReadUInt16BigEndian(fmt.Payload.AsSpan(1)));
            Assert.Equal(44100u, BinaryPrimitives.ReadUInt32BigEndian(fmt.Payload.AsSpan(5)));
            server.WaitFor(ClientMessage.Qemu, 5000, m => m.Payload[0] == QemuMessage.Audio && BinaryPrimitives.ReadUInt16BigEndian(m.Payload.AsSpan(1)) == QemuMessage.AudioEnable);

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.AudioData += d => tcs.TrySetResult(d);
            server.SendAudioData(new byte[] { 1, 2, 3, 4 });
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Server_disconnect_raises_disconnected_once()
    {
        var (server, client) = await ConnectAsync();
        using (client)
        {
            int count = 0;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += _ => { Interlocked.Increment(ref count); tcs.TrySetResult(); };
            server.Dispose();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Disconnect();
            await Task.Delay(100);
            Assert.Equal(1, count);
            Assert.False(client.IsConnected);
        }
    }
}
