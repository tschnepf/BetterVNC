using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using BetterVnc.Protocol.Decoders;
using BetterVnc.Protocol.Security;

namespace BetterVnc.Protocol;

/// <summary>
/// RFB 3.3/3.7/3.8 client. Call <see cref="ConnectAsync"/>; after it completes the framebuffer is
/// initialised and a background receive thread raises events. All events are raised on that
/// thread, so UI consumers must marshal to their dispatcher.
/// </summary>
public sealed class VncClient : IDisposable
{
    private readonly VncConnectionOptions _options;
    private TcpClient? _tcp;
    private Stream? _stream;
    private RfbReader? _reader;
    private readonly object _sendLock = new();
    private readonly RfbMessageBuilder _msg = new();
    private Thread? _receiveThread;
    private volatile bool _closing;
    private int _disconnectedRaised;

    private readonly TightDecoder _tight;
    private readonly ZrleDecoder _zrle = new();
    private readonly ZlibDecoder _zlib = new();

    // Extended clipboard state
    private bool _serverClipboardCaps;
    private uint _serverClipboardFormats;
    private string? _pendingLocalClipboardText;
    private bool _clipboardCapsSent;

    public Framebuffer Framebuffer { get; private set; } = new(0, 0);
    public string DesktopName { get; private set; } = "";
    public ServerCapabilities Capabilities { get; } = new();
    public VncStatistics Statistics { get; } = new();
    public bool IsConnected => _stream != null && !_closing;
    public VncConnectionOptions Options => _options;

    /// <summary>Raised after every FramebufferUpdate with the rectangles that changed. Read pixels under <see cref="Framebuffer.SyncRoot"/>.</summary>
    public event Action<RfbRect[]>? FramebufferUpdated;
    public event Action<int, int>? FramebufferResized;
    public event Action<CursorShape>? CursorChanged;
    public event Action<string>? ClipboardTextReceived;
    public event Action? Bell;
    public event Action<string>? DesktopNameChanged;
    public event Action<int>? LedStateChanged;
    public event Action<Exception?>? Disconnected;
    public event Action<string>? Log;

    public event Action<QemuAudioFormat, int, int>? AudioBegin;
    public event Action<byte[]>? AudioData;
    public event Action? AudioEnd;

    /// <summary>Raised when the server asks for clipboard text (extended clipboard) and nothing is pending; the handler may call <see cref="SendClipboardText"/>.</summary>
    public event Action? ClipboardRequested;

    public VncClient(VncConnectionOptions options)
    {
        _options = options;
        _tight = new TightDecoder(options.JpegDecoder);
    }

    // ------------------------------------------------------------------ connection

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Connect(cancellationToken);
            }
            catch
            {
                Cleanup();
                throw;
            }
        }, cancellationToken);
    }

    private void Connect(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            try
            {
                tcp.ConnectAsync(_options.Host, _options.Port, timeoutCts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new VncException($"Timed out connecting to {_options.Host}:{_options.Port}");
            }
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new RfbReader(_stream);

        Handshake(ct);
        ReadServerInit();

        SendSetPixelFormat(PixelFormat.Bgrx32);
        SendSetEncodings();
        SendFramebufferUpdateRequest(false);

        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "VNC receive" };
        _receiveThread.Start();
    }

    private void Handshake(CancellationToken ct)
    {
        var r = _reader!;
        var versionLine = r.ReadString(12);
        if (!versionLine.StartsWith("RFB ")) throw new VncException($"Not an RFB server (got '{versionLine.Trim()}')");
        int major = int.Parse(versionLine.Substring(4, 3));
        int minor = int.Parse(versionLine.Substring(8, 3));
        Capabilities.ProtocolVersion = $"{major}.{minor}";
        Capabilities.IsApple = major == 3 && minor == 889;
        Emit($"Server version {major}.{minor}{(Capabilities.IsApple ? " (Apple Screen Sharing)" : "")}");

        int useMinor;
        if (major > 3 || minor >= 8) useMinor = 8;
        else if (minor == 7) useMinor = 7;
        else useMinor = 3;
        WriteRaw(Encoding.ASCII.GetBytes($"RFB 003.00{useMinor}\n"));

        byte chosen;
        if (useMinor >= 7)
        {
            int count = r.ReadU8();
            if (count == 0) throw new VncException("Server refused connection: " + ReadReason());
            var types = r.ReadBytes(count);
            Emit("Security types offered: " + string.Join(", ", types));
            chosen = ChooseSecurityType(types);
            WriteRaw(new[] { chosen });
        }
        else
        {
            uint t = r.ReadU32();
            if (t == 0) throw new VncException("Server refused connection: " + ReadReason());
            chosen = (byte)t;
        }
        Capabilities.SecurityTypeUsed = chosen;
        Emit($"Security type {chosen}");

        string? username = _options.Username;
        string? password = _options.Password;

        switch (chosen)
        {
            case SecurityType.None:
                break;
            case SecurityType.VncAuth:
            {
                if (string.IsNullOrEmpty(password))
                {
                    var c = RequestCredentials(CredentialKind.Password, "VNC password required") ?? throw new VncAuthenticationException("No password supplied");
                    password = c.Password ?? "";
                }
                var challenge = r.ReadBytes(16);
                WriteRaw(VncAuthentication.EncryptChallenge(challenge, password));
                break;
            }
            case SecurityType.AppleArd:
            {
                if (string.IsNullOrEmpty(username) || password == null)
                {
                    var c = RequestCredentials(CredentialKind.UsernameAndPassword, "macOS user name and password required") ?? throw new VncAuthenticationException("No credentials supplied");
                    username = c.Username ?? "";
                    password = c.Password ?? "";
                }
                var p = AppleAuthentication.ReadServerParameters(r);
                var resp = AppleAuthentication.CreateResponse(p, username, password);
                WriteRaw(resp.EncryptedCredentials);
                WriteRaw(resp.ClientPublicKey);
                break;
            }
            default:
                throw new VncException($"Unsupported security type {chosen}");
        }

        if (useMinor >= 8 || chosen != SecurityType.None)
        {
            uint result = r.ReadU32();
            if (result != 0)
            {
                string reason = useMinor >= 8 ? ReadReasonSafe() : "authentication failed";
                throw new VncAuthenticationException(string.IsNullOrWhiteSpace(reason) ? "Authentication failed" : reason);
            }
        }

        // ClientInit
        WriteRaw(new[] { (byte)(_options.Shared ? 1 : 0) });
    }

    private byte ChooseSecurityType(byte[] offered)
    {
        bool haveUser = !string.IsNullOrEmpty(_options.Username) || _options.CredentialProvider != null;
        bool havePassword = !string.IsNullOrEmpty(_options.Password) || _options.CredentialProvider != null;

        // Prefer Apple auth when a username is available (macOS account), then VNC password, then None.
        if (haveUser && offered.Contains(SecurityType.AppleArd) && (!offered.Contains(SecurityType.VncAuth) || !string.IsNullOrEmpty(_options.Username)))
            return SecurityType.AppleArd;
        if (offered.Contains(SecurityType.None)) return SecurityType.None;
        if (offered.Contains(SecurityType.VncAuth) && havePassword) return SecurityType.VncAuth;
        if (offered.Contains(SecurityType.AppleArd) && haveUser) return SecurityType.AppleArd;
        if (offered.Contains(SecurityType.VncAuth)) return SecurityType.VncAuth;
        throw new VncException("No supported security type offered by the server: " + string.Join(", ", offered));
    }

    private VncCredentials? RequestCredentials(CredentialKind kind, string prompt)
    {
        return _options.CredentialProvider?.Invoke(kind, prompt);
    }

    private string ReadReason()
    {
        var r = _reader!;
        int len = (int)r.ReadU32();
        return r.ReadString(Math.Min(len, 4096), Encoding.UTF8);
    }

    private string ReadReasonSafe()
    {
        try { return ReadReason(); } catch { return ""; }
    }

    private void ReadServerInit()
    {
        var r = _reader!;
        int w = r.ReadU16();
        int h = r.ReadU16();
        Capabilities.ServerNativeFormat = PixelFormat.Read(r);
        int nameLen = (int)r.ReadU32();
        var nameBytes = r.ReadBytes(Math.Min(nameLen, 65536));
        DesktopName = DecodeName(nameBytes);
        Framebuffer = new Framebuffer(w, h);
        Emit($"Desktop '{DesktopName}' {w}x{h}, server format {Capabilities.ServerNativeFormat}");
    }

    private static string DecodeName(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    // ------------------------------------------------------------------ sending

    private void WriteRaw(byte[] data)
    {
        lock (_sendLock)
        {
            var s = _stream ?? throw new VncException("Not connected");
            s.Write(data, 0, data.Length);
            s.Flush();
            Statistics.BytesSent += data.Length;
        }
    }

    private void Send(Action<RfbMessageBuilder> build)
    {
        lock (_sendLock)
        {
            var s = _stream;
            if (s == null || _closing) return;
            _msg.Reset();
            build(_msg);
            try
            {
                _msg.WriteTo(s);
                s.Flush();
                Statistics.BytesSent += _msg.Length;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // The receive thread will surface the disconnect.
            }
        }
    }

    public void SendSetPixelFormat(PixelFormat pf)
    {
        Send(b =>
        {
            b.U8(ClientMessage.SetPixelFormat).Pad(3);
            pf.Write(b);
        });
    }

    private void SendSetEncodings()
    {
        var encodings = new List<int>(_options.PreferredEncodings);
        if (_options.EnableLocalCursor) encodings.Add(RfbEncoding.Cursor);
        encodings.Add(RfbEncoding.DesktopSize);
        encodings.Add(RfbEncoding.ExtendedDesktopSize);
        encodings.Add(RfbEncoding.LastRect);
        encodings.Add(RfbEncoding.DesktopName);
        encodings.Add(RfbEncoding.CompressLevel(_options.CompressionLevel));
        if (_options.JpegQuality >= 0) encodings.Add(RfbEncoding.JpegQuality(_options.JpegQuality));
        if (_options.EnableQemuExtendedKeys) encodings.Add(RfbEncoding.QemuExtendedKeyEvent);
        if (_options.EnableQemuAudio) encodings.Add(RfbEncoding.QemuAudio);
        encodings.Add(RfbEncoding.QemuLedState);
        if (_options.EnableExtendedClipboard) encodings.Add(RfbEncoding.ExtendedClipboard);

        Send(b =>
        {
            b.U8(ClientMessage.SetEncodings).Pad(1).U16(encodings.Count);
            foreach (var e in encodings) b.S32(e);
        });
    }

    public void SendFramebufferUpdateRequest(bool incremental)
    {
        var fb = Framebuffer;
        Send(b => b.U8(ClientMessage.FramebufferUpdateRequest).U8(incremental ? 1 : 0)
                   .U16(0).U16(0).U16(fb.Width).U16(fb.Height));
    }

    public void SendFramebufferUpdateRequest(bool incremental, RfbRect rect)
    {
        Send(b => b.U8(ClientMessage.FramebufferUpdateRequest).U8(incremental ? 1 : 0)
                   .U16(rect.X).U16(rect.Y).U16(rect.Width).U16(rect.Height));
    }

    /// <summary>
    /// Sends a key event. When the server supports QEMU extended key events and a scancode is
    /// provided, the raw XT scancode travels with the keysym so the guest sees the physical key.
    /// </summary>
    public void SendKeyEvent(uint keysym, bool down, uint xtScancode = 0)
    {
        if (Capabilities.QemuExtendedKeyEvents && xtScancode != 0)
        {
            uint rfbCode = (xtScancode & 0xFF00) == 0xE000 ? (xtScancode & 0x7F) | 0x80 : xtScancode & 0xFF;
            Send(b => b.U8(ClientMessage.Qemu).U8(QemuMessage.ExtendedKeyEvent)
                       .U16(down ? 1 : 0).U32(keysym).U32(rfbCode));
        }
        else
        {
            if (keysym == 0) return;
            Send(b => b.U8(ClientMessage.KeyEvent).U8(down ? 1 : 0).Pad(2).U32(keysym));
        }
    }

    public void SendPointerEvent(int x, int y, byte buttonMask)
    {
        var fb = Framebuffer;
        x = Math.Clamp(x, 0, Math.Max(0, fb.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, fb.Height - 1));
        Send(b => b.U8(ClientMessage.PointerEvent).U8(buttonMask).U16(x).U16(y));
    }

    /// <summary>Publishes local clipboard text to the server (extended clipboard notify when supported, legacy cut text otherwise).</summary>
    public void SendClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Capabilities.ExtendedClipboard && _serverClipboardCaps)
        {
            _pendingLocalClipboardText = text;
            SendClipboardNotify(ClipboardFlags.Utf8Text);
        }
        else
        {
            var bytes = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
            Send(b => b.U8(ClientMessage.ClientCutText).Pad(3).U32((uint)bytes.Length).Bytes(bytes));
        }
    }

    private void SendClipboardCaps()
    {
        if (_clipboardCapsSent) return;
        _clipboardCapsSent = true;
        SendExtendedClipboard(ClipboardFlags.Caps | ClipboardFlags.Utf8Text, b => b.U32(1u << 24));
    }

    private void SendClipboardNotify(uint formats) => SendExtendedClipboard(ClipboardFlags.Notify | formats, null);

    private void SendClipboardRequest(uint formats) => SendExtendedClipboard(ClipboardFlags.Request | formats, null);

    private void SendClipboardProvide(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var utf8 = Encoding.UTF8.GetBytes(normalized + "\0");
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)utf8.Length);
            z.Write(len);
            z.Write(utf8);
        }
        var payload = ms.ToArray();
        SendExtendedClipboard(ClipboardFlags.Provide | ClipboardFlags.Utf8Text, b => b.Bytes(payload));
    }

    private void SendExtendedClipboard(uint flags, Action<RfbMessageBuilder>? payload)
    {
        // Extended messages carry a negative length: -(4 bytes of flags + payload).
        var body = new RfbMessageBuilder();
        body.U32(flags);
        payload?.Invoke(body);
        var bodyBytes = body.Data.ToArray();
        Send(b => b.U8(ClientMessage.ClientCutText).Pad(3).S32(-bodyBytes.Length).Bytes(bodyBytes));
    }

    public void SetAudioEnabled(bool enabled, QemuAudioFormat format = QemuAudioFormat.S16, int channels = 2, int frequency = 44100)
    {
        if (!Capabilities.QemuAudio) return;
        if (enabled)
        {
            Send(b => b.U8(ClientMessage.Qemu).U8(QemuMessage.Audio).U16(QemuMessage.AudioSetFormat)
                       .U8((byte)format).U8((byte)channels).U32((uint)frequency));
            Send(b => b.U8(ClientMessage.Qemu).U8(QemuMessage.Audio).U16(QemuMessage.AudioEnable));
        }
        else
        {
            Send(b => b.U8(ClientMessage.Qemu).U8(QemuMessage.Audio).U16(QemuMessage.AudioDisable));
        }
    }

    // ------------------------------------------------------------------ receiving

    private void ReceiveLoop()
    {
        Exception? error = null;
        try
        {
            var r = _reader!;
            while (!_closing)
            {
                byte type = r.ReadU8();
                switch (type)
                {
                    case ServerMessage.FramebufferUpdate:
                        HandleFramebufferUpdate(r);
                        break;
                    case ServerMessage.SetColourMapEntries:
                        r.Skip(1);
                        r.ReadU16();
                        int n = r.ReadU16();
                        r.Skip(n * 6);
                        break;
                    case ServerMessage.Bell:
                        Bell?.Invoke();
                        break;
                    case ServerMessage.ServerCutText:
                        HandleServerCutText(r);
                        break;
                    case ServerMessage.EndOfContinuousUpdates:
                        break;
                    case ServerMessage.ServerFence:
                        r.Skip(3);
                        uint flags = r.ReadU32();
                        int len = r.ReadU8();
                        var payload = r.ReadBytes(len);
                        // Echo the fence back as the protocol requires.
                        Send(b => b.U8(ClientMessage.ClientFence).Pad(3).U32(flags & 0x3).U8(len).Bytes(payload));
                        break;
                    case ServerMessage.Qemu:
                        HandleQemuMessage(r);
                        break;
                    default:
                        throw new VncException($"Unknown server message type {type}");
                }
                Statistics.BytesReceived = r.TotalBytesRead;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or SocketException)
        {
            error = _closing ? null : ex;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Cleanup();
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
            Disconnected?.Invoke(error);
    }

    private void HandleFramebufferUpdate(RfbReader r)
    {
        var sw = Stopwatch.StartNew();
        r.Skip(1);
        int count = r.ReadU16();
        bool resized = false;
        var fb = Framebuffer;
        lock (fb.SyncRoot)
        {
            for (int i = 0; i < count; i++)
            {
                int x = r.ReadU16();
                int y = r.ReadU16();
                int w = r.ReadU16();
                int h = r.ReadU16();
                int enc = r.ReadS32();
                var rect = new RfbRect(x, y, w, h);
                Statistics.Rectangles++;

                switch (enc)
                {
                    case RfbEncoding.Raw:
                        RawDecoder.Decode(r, fb, rect);
                        Statistics.RawRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.CopyRect:
                        CopyRectDecoder.Decode(r, fb, rect);
                        Statistics.CopyRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Rre:
                        RreDecoder.Decode(r, fb, rect);
                        Statistics.RreRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Hextile:
                        HextileDecoder.Decode(r, fb, rect);
                        Statistics.HextileRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Zlib:
                        _zlib.Decode(r, fb, rect);
                        Statistics.ZlibRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Tight:
                        _tight.Decode(r, fb, rect);
                        Statistics.TightRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Zrle:
                        _zrle.Decode(r, fb, rect);
                        Statistics.ZrleRects++;
                        fb.MarkDirty(rect);
                        break;
                    case RfbEncoding.Cursor:
                        Capabilities.LocalCursor = true;
                        CursorChanged?.Invoke(CursorShape.ReadRichCursor(r, rect));
                        break;
                    case RfbEncoding.DesktopSize:
                        Capabilities.DesktopResize = true;
                        fb.Resize(w, h);
                        resized = true;
                        break;
                    case RfbEncoding.ExtendedDesktopSize:
                    {
                        Capabilities.DesktopResize = true;
                        int screens = r.ReadU8();
                        r.Skip(3);
                        r.Skip(screens * 16);
                        // x = reason (0 server, 1 this client, 2 other client), y = status for client requests.
                        if (x == 1 && y != 0) break;
                        if (w != fb.Width || h != fb.Height)
                        {
                            fb.Resize(w, h);
                            resized = true;
                        }
                        break;
                    }
                    case RfbEncoding.DesktopName:
                    {
                        int len = (int)r.ReadU32();
                        DesktopName = DecodeName(r.ReadBytes(Math.Min(len, 65536)));
                        DesktopNameChanged?.Invoke(DesktopName);
                        break;
                    }
                    case RfbEncoding.LastRect:
                        i = count;
                        break;
                    case RfbEncoding.QemuExtendedKeyEvent:
                        if (!Capabilities.QemuExtendedKeyEvents) Emit("Server supports QEMU extended key events");
                        Capabilities.QemuExtendedKeyEvents = true;
                        break;
                    case RfbEncoding.QemuAudio:
                        if (!Capabilities.QemuAudio) Emit("Server supports QEMU audio");
                        Capabilities.QemuAudio = true;
                        break;
                    case RfbEncoding.QemuLedState:
                        Capabilities.QemuLedState = true;
                        LedStateChanged?.Invoke(r.ReadU8());
                        break;
                    default:
                        throw new VncException($"Server used unsupported encoding {RfbEncoding.Name(enc)} ({enc})");
                }
            }
        }

        Statistics.FramebufferUpdates++;
        Statistics.LastUpdateUtc = DateTime.UtcNow;
        Statistics.LastUpdateMilliseconds = sw.Elapsed.TotalMilliseconds;

        if (resized)
        {
            FramebufferResized?.Invoke(fb.Width, fb.Height);
            FramebufferUpdated?.Invoke(fb.TakeDirty());
            SendFramebufferUpdateRequest(false);
            return;
        }

        var dirty = fb.TakeDirty();
        if (dirty.Length > 0) FramebufferUpdated?.Invoke(dirty);
        SendFramebufferUpdateRequest(true);
    }

    private void HandleServerCutText(RfbReader r)
    {
        r.Skip(3);
        int len = r.ReadS32();
        if (len >= 0)
        {
            var text = r.ReadString(len, Encoding.Latin1).Replace("\r\n", "\n");
            ClipboardTextReceived?.Invoke(text);
            return;
        }

        int total = -len;
        if (total < 4) throw new VncException("Malformed extended clipboard message");
        uint flags = r.ReadU32();
        int rest = total - 4;
        uint action = flags & ClipboardFlags.ActionMask;
        uint formats = flags & ClipboardFlags.FormatMask;

        if ((action & ClipboardFlags.Caps) != 0)
        {
            r.Skip(rest); // per-format maximum sizes; we do not need them
            _serverClipboardCaps = true;
            _serverClipboardFormats = formats;
            Capabilities.ExtendedClipboard = true;
            Emit($"Extended clipboard supported by server (formats 0x{formats:X})");
            SendClipboardCaps();
            return;
        }

        if (action == ClipboardFlags.Provide)
        {
            var compressed = r.ReadBytes(rest);
            var inflater = new ZlibInflater();
            var data = inflater.InflateAll(compressed, 0, compressed.Length);
            var c = new ByteCursor(data);
            for (int i = 0; i < 16; i++)
            {
                if ((formats & (1u << i)) == 0) continue;
                if (c.Remaining < 4) break;
                var lenSpan = c.ReadSpan(4);
                int l = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lenSpan);
                if (l < 0 || l > c.Remaining) break;
                var body = c.ReadSpan(l);
                if (i == 0)
                {
                    int end = body.IndexOf((byte)0);
                    if (end >= 0) body = body[..end];
                    var text = Encoding.UTF8.GetString(body).Replace("\r\n", "\n");
                    ClipboardTextReceived?.Invoke(text);
                }
            }
            return;
        }

        if (rest > 0) r.Skip(rest);
        switch (action)
        {
            case ClipboardFlags.Request:
                if ((formats & ClipboardFlags.Utf8Text) != 0)
                {
                    var text = _pendingLocalClipboardText;
                    if (text != null) SendClipboardProvide(text);
                    else ClipboardRequested?.Invoke();
                }
                break;
            case ClipboardFlags.Notify:
                if ((formats & ClipboardFlags.Utf8Text) != 0) SendClipboardRequest(ClipboardFlags.Utf8Text);
                break;
            case ClipboardFlags.Peek:
                SendClipboardNotify(_pendingLocalClipboardText != null ? ClipboardFlags.Utf8Text : 0);
                break;
        }
    }

    private void HandleQemuMessage(RfbReader r)
    {
        byte sub = r.ReadU8();
        if (sub != QemuMessage.Audio) throw new VncException($"Unknown QEMU server sub-message {sub}");
        ushort op = r.ReadU16();
        switch (op)
        {
            case QemuMessage.AudioServerBegin:
                AudioBegin?.Invoke(QemuAudioFormat.S16, 2, 44100);
                break;
            case QemuMessage.AudioServerEnd:
                AudioEnd?.Invoke();
                break;
            case QemuMessage.AudioServerData:
                int len = (int)r.ReadU32();
                var data = r.ReadBytes(len);
                AudioData?.Invoke(data);
                break;
            default:
                throw new VncException($"Unknown QEMU audio operation {op}");
        }
    }

    // ------------------------------------------------------------------ lifecycle

    private void Emit(string message) => Log?.Invoke(message);

    public void Disconnect()
    {
        _closing = true;
        Cleanup();
        // Only announce a disconnect for a session that actually came up; disposing a client whose
        // handshake failed must not overwrite the caller's error with "session ended".
        if (_receiveThread != null && Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
            Disconnected?.Invoke(null);
    }

    private void Cleanup()
    {
        _closing = true;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
