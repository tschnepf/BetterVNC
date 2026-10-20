using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BetterVnc.Protocol;

// A tiny RFB 3.8 server for development: animated test pattern, echoes pointer position and
// typed keys, supports None or VNC-password auth, Raw encoding, legacy clipboard and bell.
// Usage: BetterVnc.DemoServer [port] [password] [--size <width>x<height>]
// --size replaces the 800x500 desktop, e.g. with the size of a multi-monitor desktop that a guest agent streams part of.

var size = (W: 800, H: 500);
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--size" && i + 1 < args.Length && args[++i].Split('x') is [var sw, var sh]) size = (int.Parse(sw), int.Parse(sh));
    else positional.Add(args[i]);
}
int port = positional.Count > 0 ? int.Parse(positional[0]) : 5999;
string? password = positional.Count > 1 ? positional[1] : null;
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"Demo VNC server listening on 127.0.0.1:{port}, {size.W}x{size.H}{(password != null ? " (password protected)" : "")}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Serve(client, password, size.W, size.H));
}

static void Serve(TcpClient tcp, string? password, int W, int H)
{
    try
    {
        tcp.NoDelay = true;
        using var stream = tcp.GetStream();
        var r = new RfbReader(stream);
        void Write(ReadOnlySpan<byte> d) => stream.Write(d);

        Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        r.ReadString(12);
        Write(password == null ? new byte[] { 1, SecurityType.None } : new byte[] { 1, SecurityType.VncAuth });
        byte chosen = r.ReadU8();
        if (chosen == SecurityType.VncAuth)
        {
            var challenge = new byte[16];
            Random.Shared.NextBytes(challenge);
            Write(challenge);
            var response = r.ReadBytes(16);
            var expected = BetterVnc.Protocol.Security.VncAuthentication.EncryptChallenge(challenge, password!);
            if (!response.AsSpan().SequenceEqual(expected))
            {
                var b = new RfbMessageBuilder();
                var reason = Encoding.UTF8.GetBytes("Wrong password");
                b.U32(1).U32((uint)reason.Length).Bytes(reason);
                Write(b.Data);
                return;
            }
        }
        Write(new byte[] { 0, 0, 0, 0 });
        r.ReadU8(); // shared

        var init = new RfbMessageBuilder();
        init.U16(W).U16(H);
        PixelFormat.Bgrx32.Write(init);
        var name = Encoding.UTF8.GetBytes("BetterVNC demo server");
        init.U32((uint)name.Length).Bytes(name);
        Write(init.Data);

        int mouseX = W / 2, mouseY = H / 2;
        byte buttons = 0;
        var typed = new StringBuilder();
        var frame = new byte[W * H * 4];
        var sendLock = new object();
        int tick = 0;

        void SendFrame()
        {
            tick++;
            RenderPattern(frame, W, H, tick, mouseX, mouseY, buttons, typed.ToString());
            var b = new RfbMessageBuilder();
            b.U8(ServerMessage.FramebufferUpdate).Pad(1).U16(1);
            b.U16(0).U16(0).U16(W).U16(H).S32(RfbEncoding.Raw).Bytes(frame);
            lock (sendLock) Write(b.Data);
        }

        while (true)
        {
            byte type = r.ReadU8();
            switch (type)
            {
                case ClientMessage.SetPixelFormat:
                    r.Skip(3);
                    var pf = PixelFormat.Read(r);
                    if (pf != PixelFormat.Bgrx32) Console.WriteLine($"Client asked for {pf}; demo server only sends Bgrx32");
                    break;
                case ClientMessage.SetEncodings:
                    r.Skip(1);
                    int n = r.ReadU16();
                    var encs = new List<int>();
                    for (int i = 0; i < n; i++) encs.Add(r.ReadS32());
                    Console.WriteLine("Client encodings: " + string.Join(", ", encs.Select(RfbEncoding.Name)));
                    break;
                case ClientMessage.FramebufferUpdateRequest:
                    r.ReadBytes(9);
                    Thread.Sleep(16); // ~60 fps cap
                    SendFrame();
                    break;
                case ClientMessage.KeyEvent:
                {
                    bool down = r.ReadU8() != 0;
                    r.Skip(2);
                    uint keysym = r.ReadU32();
                    if (down) AppendKey(typed, keysym);
                    Console.WriteLine($"Key {(down ? "down" : "up  ")} 0x{keysym:X}");
                    break;
                }
                case ClientMessage.PointerEvent:
                {
                    byte pressed = r.ReadU8();
                    mouseX = r.ReadU16();
                    mouseY = r.ReadU16();
                    // Button changes only (wheel clicks are a press and a release), so a moving pointer does not flood the log.
                    if (pressed != buttons) Console.WriteLine($"Pointer buttons 0x{pressed:X2} at {mouseX},{mouseY}");
                    buttons = pressed;
                    break;
                }
                case ClientMessage.ClientCutText:
                {
                    r.Skip(3);
                    int len = r.ReadS32();
                    if (len >= 0)
                    {
                        var text = r.ReadString(len);
                        Console.WriteLine($"Clipboard from client: {text}");
                        // Echo it back upper-cased so both directions can be observed.
                        var echo = Encoding.Latin1.GetBytes(text.ToUpperInvariant());
                        var b = new RfbMessageBuilder();
                        b.U8(ServerMessage.ServerCutText).Pad(3).U32((uint)echo.Length).Bytes(echo);
                        lock (sendLock) Write(b.Data);
                    }
                    else
                    {
                        r.Skip(-len);
                    }
                    break;
                }
                case ClientMessage.Qemu:
                {
                    byte sub = r.ReadU8();
                    if (sub == QemuMessage.ExtendedKeyEvent)
                    {
                        bool down = r.ReadU16() != 0;
                        uint keysym = r.ReadU32(), keycode = r.ReadU32();
                        if (down) AppendKey(typed, keysym);
                        Console.WriteLine($"Key {(down ? "down" : "up  ")} 0x{keysym:X} (XT 0x{keycode:X})");
                    }
                    else { ushort op = r.ReadU16(); if (op == QemuMessage.AudioSetFormat) r.ReadBytes(6); }
                    break;
                }
                default:
                    Console.WriteLine($"Unknown client message {type}");
                    return;
            }
        }
    }
    catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
    {
        Console.WriteLine("Client disconnected");
    }
    finally
    {
        tcp.Dispose();
    }
}

static void AppendKey(StringBuilder typed, uint keysym)
{
    if (keysym == Keysym.BackSpace) { if (typed.Length > 0) typed.Length--; }
    else if (keysym == Keysym.Return) typed.Append('\n');
    else if (keysym >= 0x20 && keysym < 0x7F) typed.Append((char)keysym);
    else if ((keysym & 0x01000000) != 0) typed.Append(char.ConvertFromUtf32((int)(keysym & 0xFFFFFF)));
    if (typed.Length > 60) typed.Remove(0, typed.Length - 60);
}

static void RenderPattern(byte[] frame, int w, int h, int tick, int mx, int my, byte buttons, string typed)
{
    // Diagonal gradient background with a grid.
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bool grid = x % 50 == 0 || y % 50 == 0;
            frame[i + 0] = (byte)(grid ? 90 : 40 + (y * 60 / h));      // B
            frame[i + 1] = (byte)(grid ? 90 : 30 + (x * 50 / w));      // G
            frame[i + 2] = (byte)(grid ? 90 : 25);                     // R
            frame[i + 3] = 0;
        }
    }
    // Moving square.
    int sx = (tick * 3) % (w - 60), sy = (int)(h / 2 + Math.Sin(tick / 20.0) * (h / 3)) - 30;
    FillRect(frame, w, h, sx, sy, 60, 60, 0x4F8CFF);
    // Pointer crosshair; red when a button is held.
    uint c = buttons != 0 ? 0xE5484Du : 0x3DD68Cu;
    FillRect(frame, w, h, mx - 12, my - 1, 25, 3, c);
    FillRect(frame, w, h, mx - 1, my - 12, 3, 25, c);
    // Typed text rendered as 5x7 blocks (ASCII only).
    int tx = 20, ty = 20;
    foreach (var ch in typed)
    {
        if (ch == '\n') { tx = 20; ty += 24; continue; }
        DrawGlyph(frame, w, h, tx, ty, ch);
        tx += 18;
        if (tx > w - 40) { tx = 20; ty += 24; }
    }
}

static void FillRect(byte[] frame, int w, int h, int x0, int y0, int rw, int rh, uint rgb)
{
    for (int y = Math.Max(0, y0); y < Math.Min(h, y0 + rh); y++)
        for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + rw); x++)
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan((y * w + x) * 4), rgb);
}

static void DrawGlyph(byte[] frame, int w, int h, int x, int y, char ch)
{
    // Minimal 3x5 font: digits and upper-case letters; everything else is a box.
    ulong bits = ch switch
    {
        'A' => 0b010_101_111_101_101, 'B' => 0b110_101_110_101_110, 'C' => 0b011_100_100_100_011,
        'D' => 0b110_101_101_101_110, 'E' => 0b111_100_110_100_111, 'F' => 0b111_100_110_100_100,
        'G' => 0b011_100_101_101_011, 'H' => 0b101_101_111_101_101, 'I' => 0b111_010_010_010_111,
        'J' => 0b001_001_001_101_010, 'K' => 0b101_110_100_110_101, 'L' => 0b100_100_100_100_111,
        'M' => 0b101_111_111_101_101, 'N' => 0b110_101_101_101_101, 'O' => 0b010_101_101_101_010,
        'P' => 0b110_101_110_100_100, 'Q' => 0b010_101_101_111_011, 'R' => 0b110_101_110_101_101,
        'S' => 0b011_100_010_001_110, 'T' => 0b111_010_010_010_010, 'U' => 0b101_101_101_101_111,
        'V' => 0b101_101_101_101_010, 'W' => 0b101_101_111_111_101, 'X' => 0b101_101_010_101_101,
        'Y' => 0b101_101_010_010_010, 'Z' => 0b111_001_010_100_111, ' ' => 0,
        '0' => 0b111_101_101_101_111, '1' => 0b010_110_010_010_111, '2' => 0b111_001_111_100_111,
        '3' => 0b111_001_111_001_111, '4' => 0b101_101_111_001_001, '5' => 0b111_100_111_001_111,
        '6' => 0b111_100_111_101_111, '7' => 0b111_001_001_001_001, '8' => 0b111_101_111_101_111,
        '9' => 0b111_101_111_001_111,
        _ => 0b111_101_101_101_111,
    };
    if (char.IsLower(ch)) { DrawGlyph(frame, w, h, x, y, char.ToUpperInvariant(ch)); return; }
    for (int row = 0; row < 5; row++)
        for (int col = 0; col < 3; col++)
            if ((bits & (1UL << ((4 - row) * 3 + (2 - col)))) != 0)
                FillRect(frame, w, h, x + col * 4, y + row * 4, 4, 4, 0xFFFFFF);
}
