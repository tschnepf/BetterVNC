namespace BetterVnc.Protocol;

/// <summary>X11 keysym values used by the RFB KeyEvent message.</summary>
public static class Keysym
{
    public const uint BackSpace = 0xFF08;
    public const uint Tab = 0xFF09;
    public const uint Linefeed = 0xFF0A;
    public const uint Clear = 0xFF0B;
    public const uint Return = 0xFF0D;
    public const uint Pause = 0xFF13;
    public const uint ScrollLock = 0xFF14;
    public const uint SysReq = 0xFF15;
    public const uint Escape = 0xFF1B;
    public const uint Delete = 0xFFFF;

    public const uint Home = 0xFF50;
    public const uint Left = 0xFF51;
    public const uint Up = 0xFF52;
    public const uint Right = 0xFF53;
    public const uint Down = 0xFF54;
    public const uint PageUp = 0xFF55;
    public const uint PageDown = 0xFF56;
    public const uint End = 0xFF57;
    public const uint Begin = 0xFF58;

    public const uint Select = 0xFF60;
    public const uint Print = 0xFF61;
    public const uint Execute = 0xFF62;
    public const uint Insert = 0xFF63;
    public const uint Undo = 0xFF65;
    public const uint Redo = 0xFF66;
    public const uint Menu = 0xFF67;
    public const uint Find = 0xFF68;
    public const uint Cancel = 0xFF69;
    public const uint Help = 0xFF6A;
    public const uint Break = 0xFF6B;
    public const uint NumLock = 0xFF7F;

    public const uint KpSpace = 0xFF80;
    public const uint KpTab = 0xFF89;
    public const uint KpEnter = 0xFF8D;
    public const uint KpHome = 0xFF95;
    public const uint KpLeft = 0xFF96;
    public const uint KpUp = 0xFF97;
    public const uint KpRight = 0xFF98;
    public const uint KpDown = 0xFF99;
    public const uint KpPageUp = 0xFF9A;
    public const uint KpPageDown = 0xFF9B;
    public const uint KpEnd = 0xFF9C;
    public const uint KpBegin = 0xFF9D;
    public const uint KpInsert = 0xFF9E;
    public const uint KpDelete = 0xFF9F;
    public const uint KpEqual = 0xFFBD;
    public const uint KpMultiply = 0xFFAA;
    public const uint KpAdd = 0xFFAB;
    public const uint KpSeparator = 0xFFAC;
    public const uint KpSubtract = 0xFFAD;
    public const uint KpDecimal = 0xFFAE;
    public const uint KpDivide = 0xFFAF;
    public const uint Kp0 = 0xFFB0;
    public const uint Kp9 = 0xFFB9;

    public const uint F1 = 0xFFBE;
    public const uint F24 = 0xFFD5;

    public const uint ShiftL = 0xFFE1;
    public const uint ShiftR = 0xFFE2;
    public const uint ControlL = 0xFFE3;
    public const uint ControlR = 0xFFE4;
    public const uint CapsLock = 0xFFE5;
    public const uint ShiftLock = 0xFFE6;
    public const uint MetaL = 0xFFE7;
    public const uint MetaR = 0xFFE8;
    public const uint AltL = 0xFFE9;
    public const uint AltR = 0xFFEA;
    public const uint SuperL = 0xFFEB;
    public const uint SuperR = 0xFFEC;
    public const uint HyperL = 0xFFED;
    public const uint HyperR = 0xFFEE;

    public const uint IsoLevel3Shift = 0xFE03; // AltGr
    public const uint IsoLeftTab = 0xFE20;

    public const uint Space = 0x0020;

    public const uint XF86AudioLowerVolume = 0x1008FF11;
    public const uint XF86AudioMute = 0x1008FF12;
    public const uint XF86AudioRaiseVolume = 0x1008FF13;
    public const uint XF86AudioPlay = 0x1008FF14;
    public const uint XF86AudioStop = 0x1008FF15;
    public const uint XF86AudioPrev = 0x1008FF16;
    public const uint XF86AudioNext = 0x1008FF17;

    /// <summary>Maps a Unicode code point to a keysym (Latin-1 direct, otherwise the 0x01000000 Unicode range).</summary>
    public static uint FromUnicode(int codePoint)
    {
        if (codePoint < 0x20) return 0;
        if (codePoint < 0x100) return (uint)codePoint; // Latin-1 keysyms equal their code points
        return codePoint switch
        {
            0x20AC => 0x20AC, // EuroSign has its own legacy keysym
            _ => 0x01000000u | (uint)codePoint,
        };
    }
}
