using BetterVnc.Protocol.Decoders;

namespace BetterVnc.Protocol;

public sealed record VncCredentials(string? Username, string? Password);

public enum CredentialKind
{
    /// <summary>Classic VNC password only.</summary>
    Password,
    /// <summary>Username and password (Apple Remote Desktop / macOS account).</summary>
    UsernameAndPassword,
}

public sealed class VncConnectionOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5900;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Invoked (on the connection thread) when the server needs credentials that were not supplied or were rejected.</summary>
    public Func<CredentialKind, string, VncCredentials?>? CredentialProvider { get; set; }

    public bool Shared { get; set; } = true;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tight compression level 0-9 sent as a pseudo-encoding. 1 is a good LAN default. Change at run time with <see cref="VncClient.SetPictureQuality"/>.</summary>
    public int CompressionLevel { get; set; } = 1;

    /// <summary>Tight JPEG quality 0-9, or -1 to request lossless only (recommended on LAN). Change at run time with <see cref="VncClient.SetPictureQuality"/>.</summary>
    public int JpegQuality { get; set; } = -1;

    public bool EnableExtendedClipboard { get; set; } = true;
    public bool EnableQemuExtendedKeys { get; set; } = true;
    public bool EnableQemuAudio { get; set; } = true;
    public bool EnableLocalCursor { get; set; } = true;

    /// <summary>Encodings in order of preference; defaults favour lossless Tight on a LAN.</summary>
    public List<int> PreferredEncodings { get; set; } = new()
    {
        RfbEncoding.Tight,
        RfbEncoding.Zrle,
        RfbEncoding.Hextile,
        RfbEncoding.Zlib,
        RfbEncoding.Rre,
        RfbEncoding.CopyRect,
        RfbEncoding.Raw,
    };

    public IJpegDecoder? JpegDecoder { get; set; }
}

public sealed class ServerCapabilities
{
    public bool QemuExtendedKeyEvents { get; internal set; }
    public bool QemuAudio { get; internal set; }
    public bool QemuLedState { get; internal set; }
    public bool ExtendedClipboard { get; internal set; }
    public bool LocalCursor { get; internal set; }
    public bool DesktopResize { get; internal set; }
    public string ProtocolVersion { get; internal set; } = "";
    public byte SecurityTypeUsed { get; internal set; }
    public bool IsApple { get; internal set; }
    public PixelFormat ServerNativeFormat { get; internal set; }

    public override string ToString() =>
        $"RFB {ProtocolVersion}, security {SecurityTypeUsed}{(IsApple ? " (Apple)" : "")}, extkeys={QemuExtendedKeyEvents}, audio={QemuAudio}, extclip={ExtendedClipboard}, cursor={LocalCursor}";
}

/// <summary>Running counters, safe to read from any thread (values may be slightly stale).</summary>
public sealed class VncStatistics
{
    public long BytesReceived;
    public long BytesSent;
    public long FramebufferUpdates;
    public long Rectangles;
    public long TightRects, ZrleRects, HextileRects, RawRects, CopyRects, ZlibRects, RreRects;
    /// <summary>Tight rectangles that came as JPEG. Stays 0 when the server ignores the JPEG quality hint (QEMU without <c>lossy=on</c>).</summary>
    public long TightJpegRects;
    public DateTime LastUpdateUtc;
    public double LastUpdateMilliseconds;
}
