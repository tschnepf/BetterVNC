namespace BetterVnc.Protocol;

/// <summary>XT (scan code set 1) key numbers as <see cref="VncClient.SendKeyEvent"/> takes them: 0xE0xx for E0-prefixed keys.</summary>
public static class XtScancode
{
    /// <summary>
    /// The XT code for a key as Windows reports it in WM_KEYDOWN or a low-level hook: lParam scan code plus extended flag.
    /// Windows swaps two keys against XT: NumLock (a plain 45 from the keyboard) arrives as 0x45 with the extended flag,
    /// and Pause (E1 1D 45) arrives as a plain 0x45. QEMU numbers Pause E0 46, the code Ctrl+Break already arrives as.
    /// </summary>
    public static uint FromWindows(int scancode, bool extended) => (scancode, extended) switch
    {
        (0x45, true) => 0x45,
        (0x45, false) => 0xE046,
        _ => extended ? (uint)(0xE000 | scancode) : (uint)scancode,
    };
}
