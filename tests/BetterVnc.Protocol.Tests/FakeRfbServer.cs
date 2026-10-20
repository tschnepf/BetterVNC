using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using BetterVnc.Protocol;
using BetterVnc.Protocol.Security;

namespace BetterVnc.Protocol.Tests;

/// <summary>A scripted RFB 3.8 server on loopback used to drive the client through real sockets.</summary>
public sealed class FakeRfbServer : IDisposable
{
    public sealed record ClientMsg(byte Type, byte[] Payload);

    private readonly TcpListener _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private RfbReader? _reader;
    private readonly BlockingCollection<ClientMsg> _messages = new();
    private readonly TaskCompletionSource _initialised = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();

    public int Port { get; }
    public string Version { get; set; } = "RFB 003.008\n";
    public byte[] SecurityTypes { get; set; } = { SecurityType.None };
    public string? VncPassword { get; set; }
    public (string User, string Password)? AppleCredentials { get; set; }
    public int Width { get; set; } = 64;
    public int Height { get; set; } = 48;
    public string Name { get; set; } = "fake desktop";

    public PixelFormat? ClientPixelFormat { get; private set; }
    public List<int> ClientEncodings { get; } = new();
    public Exception? Error { get; private set; }
    public Task Initialised => _initialised.Task;

    public FakeRfbServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(Serve);
    }

    private void Serve()
    {
        try
        {
            _client = _listener.AcceptTcpClient();
            _stream = _client.GetStream();
            _reader = new RfbReader(_stream);
            DoHandshake();
            while (true)
            {
                var msg = ReadClientMessage();
                if (msg == null) break;
                _messages.Add(msg);
            }
        }
        catch (Exception ex)
        {
            Error = ex;
            _initialised.TrySetException(ex);
        }
        finally
        {
            _messages.CompleteAdding();
        }
    }

    private void DoHandshake()
    {
        var r = _reader!;
        Write(Encoding.ASCII.GetBytes(Version));
        var clientVersion = r.ReadString(12);
        if (clientVersion != "RFB 003.008\n") throw new Exception($"Client version {clientVersion.Trim()}");

        Write(new[] { (byte)SecurityTypes.Length }.Concat(SecurityTypes).ToArray());
        byte chosen = r.ReadU8();
        switch (chosen)
        {
            case SecurityType.None:
                break;
            case SecurityType.VncAuth:
            {
                var challenge = new byte[16];
                Random.Shared.NextBytes(challenge);
                Write(challenge);
                var response = r.ReadBytes(16);
                var expected = VncAuthentication.EncryptChallenge(challenge, VncPassword ?? "");
                if (!response.AsSpan().SequenceEqual(expected))
                {
                    WriteU32(1);
                    var reason = Encoding.UTF8.GetBytes("bad password");
                    WriteU32((uint)reason.Length);
                    Write(reason);
                    throw new Exception("auth failed");
                }
                break;
            }
            case SecurityType.AppleArd:
            {
                // 2^127 - 1 is prime; small but enough to exercise the arithmetic.
                var prime = BigInteger.Pow(2, 127) - 1;
                var primeBytes = AppleAuthentication.ToFixedBigEndian(prime, 16);
                var serverPriv = new BigInteger(123456789012345678UL);
                var serverPub = BigInteger.ModPow(2, serverPriv, prime);
                var b = new RfbMessageBuilder();
                b.U16(2).U16(16).Bytes(primeBytes).Bytes(AppleAuthentication.ToFixedBigEndian(serverPub, 16));
                Write(b.Data.ToArray());
                var cipher = r.ReadBytes(128);
                var clientPub = r.ReadBytes(16);
                var p = new AppleAuthentication.ServerParameters(2, 16, primeBytes, AppleAuthentication.ToFixedBigEndian(serverPub, 16));
                var (user, pw) = AppleAuthentication.DecryptCredentials(p, serverPriv, new AppleAuthentication.ClientResponse(cipher, clientPub));
                if (AppleCredentials is not { } creds || creds.User != user || creds.Password != pw)
                {
                    WriteU32(1);
                    var reason = Encoding.UTF8.GetBytes("bad credentials");
                    WriteU32((uint)reason.Length);
                    Write(reason);
                    throw new Exception("apple auth failed");
                }
                break;
            }
            default:
                throw new Exception($"client chose {chosen}");
        }
        WriteU32(0); // SecurityResult OK

        r.ReadU8(); // ClientInit shared flag

        var init = new RfbMessageBuilder();
        init.U16(Width).U16(Height);
        new PixelFormat(32, 24, false, true, 255, 255, 255, 0, 8, 16).Write(init); // deliberately different from client's
        var name = Encoding.UTF8.GetBytes(Name);
        init.U32((uint)name.Length).Bytes(name);
        Write(init.Data.ToArray());
        _initialised.TrySetResult();
    }

    private ClientMsg? ReadClientMessage()
    {
        var r = _reader!;
        byte type;
        try { type = r.ReadU8(); }
        catch (EndOfStreamException) { return null; }
        catch (IOException) { return null; }
        switch (type)
        {
            case ClientMessage.SetPixelFormat:
            {
                r.Skip(3);
                var pf = PixelFormat.Read(r);
                ClientPixelFormat = pf;
                return new ClientMsg(type, Array.Empty<byte>());
            }
            case ClientMessage.SetEncodings:
            {
                r.Skip(1);
                int n = r.ReadU16();
                ClientEncodings.Clear();
                for (int i = 0; i < n; i++) ClientEncodings.Add(r.ReadS32());
                return new ClientMsg(type, Array.Empty<byte>());
            }
            case ClientMessage.FramebufferUpdateRequest:
                return new ClientMsg(type, r.ReadBytes(9));
            case ClientMessage.KeyEvent:
                return new ClientMsg(type, r.ReadBytes(7));
            case ClientMessage.PointerEvent:
                return new ClientMsg(type, r.ReadBytes(5));
            case ClientMessage.ClientCutText:
            {
                r.Skip(3);
                int len = r.ReadS32();
                var payload = new byte[4 + Math.Abs(len)];
                BinaryPrimitives.WriteInt32BigEndian(payload, len);
                r.ReadBytes(payload.AsSpan(4));
                return new ClientMsg(type, payload);
            }
            case ClientMessage.Qemu:
            {
                byte sub = r.ReadU8();
                if (sub == QemuMessage.ExtendedKeyEvent) return new ClientMsg(type, new[] { sub }.Concat(r.ReadBytes(10)).ToArray());
                ushort op = r.ReadU16();
                var rest = op == QemuMessage.AudioSetFormat ? r.ReadBytes(6) : Array.Empty<byte>();
                var buf = new byte[3 + rest.Length];
                buf[0] = sub;
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), op);
                rest.CopyTo(buf, 3);
                return new ClientMsg(type, buf);
            }
            default:
                throw new Exception($"Unexpected client message {type}");
        }
    }

    public ClientMsg WaitFor(byte type, int timeoutMs = 5000, Func<ClientMsg, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            if (!_messages.TryTake(out var msg, remaining))
                throw new TimeoutException($"No client message of type {type} within {timeoutMs}ms" + (Error != null ? $" (server error: {Error.Message})" : ""));
            if (msg.Type == type && (predicate == null || predicate(msg))) return msg;
        }
    }

    // ------------------------------------------------------------------ writing

    public void Write(byte[] data)
    {
        lock (_writeLock)
        {
            _stream!.Write(data, 0, data.Length);
            _stream.Flush();
        }
    }

    private void WriteU32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        Write(b);
    }

    public sealed record Rect(int X, int Y, int W, int H, int Encoding, byte[] Data);

    public void SendUpdate(params Rect[] rects)
    {
        var b = new RfbMessageBuilder();
        b.U8(ServerMessage.FramebufferUpdate).Pad(1).U16(rects.Length);
        foreach (var r in rects)
            b.U16(r.X).U16(r.Y).U16(r.W).U16(r.H).S32(r.Encoding).Bytes(r.Data);
        Write(b.Data.ToArray());
    }

    public void SendServerCutText(string latin1Text)
    {
        var bytes = Encoding.Latin1.GetBytes(latin1Text);
        var b = new RfbMessageBuilder();
        b.U8(ServerMessage.ServerCutText).Pad(3).U32((uint)bytes.Length).Bytes(bytes);
        Write(b.Data.ToArray());
    }

    public void SendExtendedClipboard(uint flags, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var b = new RfbMessageBuilder();
        b.U8(ServerMessage.ServerCutText).Pad(3).S32(-(4 + payload.Length)).U32(flags).Bytes(payload);
        Write(b.Data.ToArray());
    }

    public void SendBell() => Write(new[] { ServerMessage.Bell });

    public void SendAudioData(byte[] pcm)
    {
        var b = new RfbMessageBuilder();
        b.U8(ServerMessage.Qemu).U8(QemuMessage.Audio).U16(QemuMessage.AudioServerBegin);
        b.U8(ServerMessage.Qemu).U8(QemuMessage.Audio).U16(QemuMessage.AudioServerData).U32((uint)pcm.Length).Bytes(pcm);
        Write(b.Data.ToArray());
    }

    public static byte[] ZlibFinish(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// A persistent zlib stream with a sync flush after each chunk, as real servers use. Uses the
    /// real zlib via <see cref="System.IO.Compression.ZLibStream"/> so every chunk ends with the same
    /// empty stored block (<c>00 00 FF FF</c>) that QEMU and TigerVNC emit; SharpZipLib's own
    /// <c>Deflater.Flush</c> lays the tail out differently and hid a decoder bug.
    /// </summary>
    public sealed class ZlibStreamEncoder
    {
        private readonly MemoryStream _out = new();
        private readonly System.IO.Compression.ZLibStream _z;

        public ZlibStreamEncoder()
        {
            _z = new System.IO.Compression.ZLibStream(_out, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
        }

        public byte[] Compress(byte[] data)
        {
            _out.SetLength(0);
            _z.Write(data);
            _z.Flush(); // Z_SYNC_FLUSH
            return _out.ToArray();
        }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _listener.Stop();
    }
}
