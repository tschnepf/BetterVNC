namespace BetterVnc.Protocol;

public static class RfbEncoding
{
    public const int Raw = 0;
    public const int CopyRect = 1;
    public const int Rre = 2;
    public const int Hextile = 5;
    public const int Zlib = 6;
    public const int Tight = 7;
    public const int Zrle = 16;
    public const int Zywrle = 17;

    // Pseudo-encodings
    public const int Cursor = -239;
    public const int XCursor = -240;
    public const int DesktopSize = -223;
    public const int LastRect = -224;
    public const int ExtendedDesktopSize = -308;
    public const int DesktopName = -307;
    public const int Fence = -312;
    public const int ContinuousUpdates = -313;
    public const int CursorWithAlpha = -314;
    public const int QemuExtendedKeyEvent = -258;
    public const int QemuAudio = -259;
    public const int QemuLedState = -261;
    public const int TightPng = -260;
    public const int ExtendedClipboard = unchecked((int)0xC0A1E5CE);
    public const int VMwareCursor = 0x574D5664;
    public const int Wmvi = 0x574D5669;

    /// <summary>Tight compression level pseudo-encodings: -256 (level 0) .. -247 (level 9).</summary>
    public static int CompressLevel(int level) => -256 + Math.Clamp(level, 0, 9);

    /// <summary>Tight JPEG quality pseudo-encodings: -32 (quality 0) .. -23 (quality 9).</summary>
    public static int JpegQuality(int quality) => -32 + Math.Clamp(quality, 0, 9);

    public static string Name(int encoding) => encoding switch
    {
        Raw => "Raw",
        CopyRect => "CopyRect",
        Rre => "RRE",
        Hextile => "Hextile",
        Zlib => "Zlib",
        Tight => "Tight",
        Zrle => "ZRLE",
        Zywrle => "ZYWRLE",
        Cursor => "Cursor",
        DesktopSize => "DesktopSize",
        LastRect => "LastRect",
        ExtendedDesktopSize => "ExtendedDesktopSize",
        QemuExtendedKeyEvent => "QemuExtKey",
        QemuAudio => "QemuAudio",
        QemuLedState => "QemuLedState",
        ExtendedClipboard => "ExtendedClipboard",
        _ => encoding.ToString(),
    };
}

public static class SecurityType
{
    public const byte Invalid = 0;
    public const byte None = 1;
    public const byte VncAuth = 2;
    public const byte Tight = 16;
    public const byte VeNCrypt = 19;
    public const byte AppleArd = 30;
    public const byte Apple33 = 33;
    public const byte Apple35 = 35;
    public const byte Apple36 = 36;
}

public static class ClientMessage
{
    public const byte SetPixelFormat = 0;
    public const byte SetEncodings = 2;
    public const byte FramebufferUpdateRequest = 3;
    public const byte KeyEvent = 4;
    public const byte PointerEvent = 5;
    public const byte ClientCutText = 6;
    public const byte EnableContinuousUpdates = 150;
    public const byte ClientFence = 248;
    public const byte Qemu = 255;
}

public static class ServerMessage
{
    public const byte FramebufferUpdate = 0;
    public const byte SetColourMapEntries = 1;
    public const byte Bell = 2;
    public const byte ServerCutText = 3;
    public const byte EndOfContinuousUpdates = 150;
    public const byte ServerFence = 248;
    public const byte Qemu = 255;
}

public static class QemuMessage
{
    public const byte ExtendedKeyEvent = 0;
    public const byte Audio = 1;

    public const ushort AudioEnable = 0;
    public const ushort AudioDisable = 1;
    public const ushort AudioSetFormat = 2;

    public const ushort AudioServerEnd = 0;
    public const ushort AudioServerBegin = 1;
    public const ushort AudioServerData = 2;
}

/// <summary>Flags used by the TigerVNC / QEMU extended clipboard extension.</summary>
public static class ClipboardFlags
{
    public const uint Utf8Text = 1u << 0;
    public const uint Rtf = 1u << 1;
    public const uint Html = 1u << 2;
    public const uint Dib = 1u << 3;
    public const uint Files = 1u << 4;
    public const uint FormatMask = 0x0000FFFF;

    public const uint Caps = 1u << 24;
    public const uint Request = 1u << 25;
    public const uint Peek = 1u << 26;
    public const uint Notify = 1u << 27;
    public const uint Provide = 1u << 28;
    public const uint ActionMask = 0xFF000000;
}

/// <summary>Pointer button bit masks for PointerEvent.</summary>
public static class PointerButton
{
    public const byte Left = 1 << 0;
    public const byte Middle = 1 << 1;
    public const byte Right = 1 << 2;
    public const byte WheelUp = 1 << 3;
    public const byte WheelDown = 1 << 4;
    public const byte WheelLeft = 1 << 5;
    public const byte WheelRight = 1 << 6;
    public const byte Back = 1 << 7;
}

public enum QemuAudioFormat : byte
{
    U8 = 0,
    S8 = 1,
    U16 = 2,
    S16 = 3,
    U32 = 4,
    S32 = 5,
}
