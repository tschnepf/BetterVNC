using System.Text;
using BetterVnc.App.Models;
using BetterVnc.Protocol;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Input;

/// <summary>A translated key: X11 keysym for KeyEvent plus the XT scancode for the QEMU extension.</summary>
public readonly record struct TranslatedKey(uint Keysym, uint Scancode);

/// <summary>
/// Converts Windows virtual-key / scancode pairs into RFB keysyms and XT scancodes.
/// Characters come from the active keyboard layout via ToUnicodeEx with Ctrl/Alt stripped, so
/// Ctrl+C yields the keysym for 'c' and the remote applies its own Ctrl semantics.
/// </summary>
public sealed class KeyboardTranslator
{
    public bool MacMode { get; set; }
    public MacModifierMode MacModifiers { get; set; } = MacModifierMode.Standard;

    private static readonly Dictionary<int, uint> SpecialKeys = new()
    {
        [0x08] = Keysym.BackSpace,
        [0x09] = Keysym.Tab,
        [0x0C] = Keysym.Clear,
        [0x0D] = Keysym.Return,
        [0x13] = Keysym.Pause,
        [0x14] = Keysym.CapsLock,
        [0x1B] = Keysym.Escape,
        [0x21] = Keysym.PageUp,
        [0x22] = Keysym.PageDown,
        [0x23] = Keysym.End,
        [0x24] = Keysym.Home,
        [0x25] = Keysym.Left,
        [0x26] = Keysym.Up,
        [0x27] = Keysym.Right,
        [0x28] = Keysym.Down,
        [0x2C] = Keysym.Print,
        [0x2D] = Keysym.Insert,
        [0x2E] = Keysym.Delete,
        [0x5B] = Keysym.SuperL,
        [0x5C] = Keysym.SuperR,
        [0x5D] = Keysym.Menu,
        [0x6A] = Keysym.KpMultiply,
        [0x6B] = Keysym.KpAdd,
        [0x6C] = Keysym.KpSeparator,
        [0x6D] = Keysym.KpSubtract,
        [0x6E] = Keysym.KpDecimal,
        [0x6F] = Keysym.KpDivide,
        [0x90] = Keysym.NumLock,
        [0x91] = Keysym.ScrollLock,
        [0xAD] = Keysym.XF86AudioMute,
        [0xAE] = Keysym.XF86AudioLowerVolume,
        [0xAF] = Keysym.XF86AudioRaiseVolume,
        [0xB0] = Keysym.XF86AudioNext,
        [0xB1] = Keysym.XF86AudioPrev,
        [0xB2] = Keysym.XF86AudioStop,
        [0xB3] = Keysym.XF86AudioPlay,
    };

    public TranslatedKey Translate(int vk, int scancode, bool extended)
    {
        uint xt = XtScancode.FromWindows(scancode, extended);
        uint keysym = ToKeysym(vk, scancode, extended);
        if (MacMode) keysym = ApplyMacModifiers(keysym);
        return new TranslatedKey(keysym, xt);
    }

    private uint ApplyMacModifiers(uint keysym)
    {
        switch (MacModifiers)
        {
            case MacModifierMode.CtrlAsCommand:
                return keysym switch
                {
                    Keysym.ControlL => Keysym.SuperL,
                    Keysym.ControlR => Keysym.SuperR,
                    Keysym.SuperL => Keysym.ControlL,
                    Keysym.SuperR => Keysym.ControlR,
                    _ => keysym,
                };
            case MacModifierMode.SwapWinAlt:
                return keysym switch
                {
                    Keysym.AltL => Keysym.SuperL,
                    Keysym.AltR => Keysym.SuperR,
                    Keysym.SuperL => Keysym.AltL,
                    Keysym.SuperR => Keysym.AltR,
                    _ => keysym,
                };
            default:
                return keysym;
        }
    }

    private static uint ToKeysym(int vk, int scancode, bool extended)
    {
        switch (vk)
        {
            case VK_SHIFT:
            case VK_LSHIFT:
            case VK_RSHIFT:
                return scancode == 0x36 || vk == VK_RSHIFT ? Keysym.ShiftR : Keysym.ShiftL;
            case VK_CONTROL:
            case VK_LCONTROL:
            case VK_RCONTROL:
                return extended || vk == VK_RCONTROL ? Keysym.ControlR : Keysym.ControlL;
            case VK_MENU:
            case VK_LMENU:
            case VK_RMENU:
                return extended || vk == VK_RMENU ? Keysym.AltR : Keysym.AltL;
            case 0x0D:
                return extended ? Keysym.KpEnter : Keysym.Return;
        }

        if (vk >= 0x60 && vk <= 0x69) return Keysym.Kp0 + (uint)(vk - 0x60);
        if (vk >= 0x70 && vk <= 0x87) return Keysym.F1 + (uint)(vk - 0x70);
        if (SpecialKeys.TryGetValue(vk, out var special)) return special;

        // Everything else is a character key: ask the layout, ignoring Ctrl and Alt (but keeping AltGr).
        var state = new byte[256];
        GetKeyboardState(state);
        bool altGr = (state[VK_RMENU] & 0x80) != 0 && (state[VK_LCONTROL] & 0x80) != 0;
        if (!altGr)
        {
            state[VK_CONTROL] = state[VK_LCONTROL] = state[VK_RCONTROL] = 0;
            state[VK_MENU] = state[VK_LMENU] = state[VK_RMENU] = 0;
        }
        state[VK_LWIN] = state[VK_RWIN] = 0;

        var layout = GetKeyboardLayout(0);
        var sb = new StringBuilder(8);
        // Flag bit 2 (0x4): do not change keyboard state (dead keys) on Windows 10 1607+.
        int n = ToUnicodeEx((uint)vk, (uint)scancode, state, sb, sb.Capacity, 1 << 2, layout);
        if (n == -1)
        {
            // Dead key: use its spacing form so the remote at least sees the accent.
            n = ToUnicodeEx((uint)vk, (uint)scancode, state, sb, sb.Capacity, 1 << 2, layout);
            if (n < 1) n = 0;
        }
        if (n >= 1)
        {
            var s = sb.ToString(0, n);
            int cp = char.ConvertToUtf32(s, 0);
            var ks = Keysym.FromUnicode(cp);
            if (ks != 0) return ks;
        }

        uint ch = MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_CHAR, layout) & 0xFFFF;
        if (ch != 0)
        {
            bool shift = (state[VK_SHIFT] & 0x80) != 0;
            bool caps = (state[VK_CAPITAL] & 1) != 0;
            int cp = (int)ch;
            if (char.IsLetter((char)cp)) cp = shift ^ caps ? char.ToUpperInvariant((char)cp) : char.ToLowerInvariant((char)cp);
            return Keysym.FromUnicode(cp);
        }
        return 0;
    }
}
