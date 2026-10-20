using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Win32.Input;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Question 3, in two parts.
/// A. Mapping: for every Windows scancode (with and without the E0 prefix) and for the keys that arrive in unusual
///    ways (Pause, NumLock, Print Screen, AltGr, Japanese, Korean, ISO, media keys), which Avalonia PhysicalKey does
///    Avalonia's own Win32 code give, and can a PhysicalKey -> XT table give back the scancode today's
///    KeyboardTranslator sends ((extended ? 0xE000 : 0) | scancode)?
/// B. Messages: with Win32Properties.AddWndProcHookCallback, does the hook see WM_KEYDOWN/WM_SYSKEYDOWN with the
///    scancode before Avalonia acts on them, and does handled = true keep them from Avalonia (no KeyDown event, no
///    menu mode after F10 or a lone Alt)? Messages are posted to the window, not typed: the PC's real keyboard is left alone.
/// </summary>
internal static class KeyTest
{
    private readonly record struct Probe(string Name, int Vk, int Scancode, bool Extended);

    private static readonly Probe[] Specials =
    {
        new("Pause", 0x13, 0x45, false), new("NumLock", 0x90, 0x45, true), new("Ctrl+Break", 0x03, 0x46, true),
        new("PrintScreen", 0x2C, 0x37, true), new("Alt+PrintScreen (SysRq)", 0x2C, 0x54, false),
        new("AltGr: LCtrl part", 0x11, 0x1D, false), new("AltGr: RAlt part", 0x12, 0x38, true), new("Right Ctrl", 0x11, 0x1D, true),
        new("Numpad Enter", 0x0D, 0x1C, true), new("Numpad /", 0x6F, 0x35, true), new("Numpad 7 (NumLock off)", 0x24, 0x47, false), new("Home", 0x24, 0x47, true),
        new("Left Win", 0x5B, 0x5B, true), new("Right Win", 0x5C, 0x5C, true), new("Menu", 0x5D, 0x5D, true),
        new("JP Hankaku/Zenkaku", 0xF3, 0x29, false), new("JP Kana", 0x15, 0x70, false), new("JP Henkan (Convert)", 0x1C, 0x79, false),
        new("JP Muhenkan (NonConvert)", 0x1D, 0x7B, false), new("JP Yen", 0xDC, 0x7D, false), new("JP Ro", 0xE2, 0x73, false),
        new("KR Hangul (0x72)", 0x15, 0x72, false), new("KR Hanja (0x71)", 0x19, 0x71, false),
        new("KR Hangul (right Alt position)", 0x15, 0x38, true), new("KR Hanja (right Ctrl position)", 0x19, 0x1D, true),
        new("ISO \\ (102nd key)", 0xE2, 0x56, false), new("BR ABNT C1", 0xC1, 0x73, false), new("BR ABNT C2 (numpad .)", 0xC2, 0x7E, false),
        new("Volume Mute", 0xAD, 0x20, true), new("Volume Down", 0xAE, 0x2E, true), new("Volume Up", 0xAF, 0x30, true),
        new("Media Next", 0xB0, 0x19, true), new("Media Prev", 0xB1, 0x10, true), new("Media Stop", 0xB2, 0x24, true), new("Media Play/Pause", 0xB3, 0x22, true),
        new("F13", 0x7C, 0x64, false), new("F24", 0x87, 0x76, false),
    };

    public static async Task<int> RunAsync()
    {
        MappingTable();
        await MessageHookAsync();
        return 0;
    }

    private static int KeyData(int scancode, bool extended, bool up = false) =>
        1 | (scancode << 16) | (extended ? 1 << 24 : 0) | (up ? (1 << 30) | (1 << 31) : 0);

    private static void MappingTable()
    {
        var hkl = Native.GetKeyboardLayout(0);
        var byKey = new Dictionary<PhysicalKey, List<uint>>();
        int none = 0, total = 0;
        var noneList = new List<string>();
        for (int ext = 0; ext < 2; ext++)
        {
            for (int sc = 1; sc < 0x80; sc++)
            {
                uint code = (uint)(ext == 1 ? 0xE000 | sc : sc);
                int vk = (int)Native.MapVirtualKeyEx(code, 3 /* MAPVK_VSC_TO_VK_EX */, hkl);
                if (vk == 0) continue;
                total++;
                var pk = KeyInterop.PhysicalKeyFromVirtualKey(vk, KeyData(sc, ext == 1));
                if (pk == PhysicalKey.None) { none++; noneList.Add($"{code:X4}/vk{vk:X2}"); continue; }
                (byKey.TryGetValue(pk, out var l) ? l : byKey[pk] = new List<uint>()).Add(code);
            }
        }
        var collisions = byKey.Where(kv => kv.Value.Count > 1).ToList();
        Console.WriteLine($"[keys A] layout {hkl.ToInt64() & 0xFFFF:X4}: {total} scancodes with a virtual key; {byKey.Count} distinct PhysicalKeys; {none} map to None" +
            (none > 0 ? $" ({string.Join(" ", noneList)})" : ""));
        Console.WriteLine($"  PhysicalKeys reached from more than one scancode: {(collisions.Count == 0 ? "none" : string.Join("; ", collisions.Select(kv => $"{kv.Key} <- {string.Join(",", kv.Value.Select(c => c.ToString("X4")))}")))}");

        Console.WriteLine("  special keys as WM_KEYDOWN delivers them (vk, scancode, extended) -> PhysicalKey -> XT today | XT from a PhysicalKey table:");
        var table = byKey.Where(kv => kv.Value.Count == 1).ToDictionary(kv => kv.Key, kv => kv.Value[0]);
        foreach (var p in Specials)
        {
            var pk = KeyInterop.PhysicalKeyFromVirtualKey(p.Vk, KeyData(p.Scancode, p.Extended));
            uint today = p.Extended ? (uint)(0xE000 | p.Scancode) : (uint)p.Scancode;
            string fromTable = table.TryGetValue(pk, out var xt) ? xt.ToString("X4") : "-";
            string mark = pk == PhysicalKey.None ? "  <- None" : fromTable != today.ToString("X4") ? "  <- differs" : "";
            Console.WriteLine($"    {p.Name,-32} vk {p.Vk:X2} sc {p.Scancode:X2}{(p.Extended ? " E0" : "   ")} -> {pk,-22} today {today:X4} | table {fromTable}{mark}");
        }
        var names = Enum.GetNames<PhysicalKey>();
        string[] want = { "IntlBackslash", "IntlRo", "IntlYen", "KanaMode", "Convert", "NonConvert", "Lang1", "Lang2", "Pause", "PrintScreen", "NumLock", "ScrollLock", "ContextMenu", "NumPadEnter", "MediaPlayPause", "AudioVolumeMute", "F24", "NumPadComma", "Power", "Sleep" };
        Console.WriteLine($"  PhysicalKey has {names.Length} members; of the W3C codes a VNC keyboard needs, missing: " +
            (want.Where(n => !names.Contains(n)).ToArray() is { Length: > 0 } miss ? string.Join(", ", miss) : "none"));
    }

    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_SYSCHAR = 0x106,
        WM_SYSCOMMAND = 0x112, WM_INITMENU = 0x116, WM_MENUCHAR = 0x120, WM_ENTERMENULOOP = 0x211, WM_EXITMENULOOP = 0x212;

    private static async Task MessageHookAsync()
    {
        var target = new Border { Background = Brushes.DarkSlateGray, Focusable = true, Child = new TextBlock { Text = "key test", Foreground = Brushes.White } };
        var window = await Program.ShowTestWindowAsync("key test", target, 400, 200);
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        var log = new List<string>();
        bool swallow = false;
        IntPtr Hook(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            string? name = msg switch
            {
                WM_KEYDOWN => "WM_KEYDOWN", WM_KEYUP => "WM_KEYUP", WM_CHAR => "WM_CHAR", WM_SYSKEYDOWN => "WM_SYSKEYDOWN", WM_SYSKEYUP => "WM_SYSKEYUP",
                WM_SYSCHAR => "WM_SYSCHAR", WM_SYSCOMMAND => "WM_SYSCOMMAND", WM_INITMENU => "WM_INITMENU", WM_MENUCHAR => "WM_MENUCHAR",
                WM_ENTERMENULOOP => "WM_ENTERMENULOOP", WM_EXITMENULOOP => "WM_EXITMENULOOP", _ => null,
            };
            if (name == null) return IntPtr.Zero;
            long lp = lParam.ToInt64();
            log.Add(msg is WM_SYSCOMMAND ? $"hook {name} 0x{wParam.ToInt64() & 0xFFF0:X4}" : $"hook {name} vk {wParam.ToInt64():X2} sc {(lp >> 16) & 0xFF:X2}{(((lp >> 24) & 1) != 0 ? " E0" : "")}");
            if (swallow && msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP or WM_CHAR or WM_SYSCHAR) handled = true;
            return IntPtr.Zero;
        }
        Win32Properties.CustomWndProcHookCallback callback = Hook;
        Win32Properties.AddWndProcHookCallback(window, callback);
        window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) => log.Add($"Avalonia KeyDown {e.Key} physical {e.PhysicalKey} symbol {Show(e.KeySymbol)} mods {e.KeyModifiers}"), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyUpEvent, (object? _, KeyEventArgs e) => log.Add($"Avalonia KeyUp {e.Key} physical {e.PhysicalKey}"), RoutingStrategies.Tunnel, handledEventsToo: true);
        target.Focus();
        await Task.Delay(200);

        async Task Send(string what, params (uint Msg, int Vk, int Sc, bool Ext)[] seq)
        {
            log.Add($"-- {what}");
            foreach (var (msg, vk, sc, ext) in seq)
            {
                Native.PostMessage(hwnd, msg, vk, KeyData(sc, ext, up: msg is WM_KEYUP or WM_SYSKEYUP));
                await Task.Delay(60);
            }
            await Task.Delay(150);
        }

        foreach (bool s in new[] { false, true })
        {
            swallow = s;
            log.Add(s ? "==== hook sets handled = true" : "==== hook leaves the messages to Avalonia");
            await Send("A", (WM_KEYDOWN, 0x41, 0x1E, false), (WM_KEYUP, 0x41, 0x1E, false));
            await Send("F10", (WM_SYSKEYDOWN, 0x79, 0x44, false), (WM_SYSKEYUP, 0x79, 0x44, false));
            await Send("Alt alone", (WM_SYSKEYDOWN, 0x12, 0x38, false), (WM_SYSKEYUP, 0x12, 0x38, false));
            await Send("Alt+X", (WM_SYSKEYDOWN, 0x12, 0x38, false), (WM_SYSKEYDOWN, 0x58, 0x2D, false), (WM_SYSKEYUP, 0x58, 0x2D, false), (WM_KEYUP, 0x12, 0x38, false));
            await Send("AltGr (LCtrl + RAlt)", (WM_KEYDOWN, 0x11, 0x1D, false), (WM_SYSKEYDOWN, 0x12, 0x38, true), (WM_KEYUP, 0x11, 0x1D, false), (WM_KEYUP, 0x12, 0x38, true));
            await Send("Numpad Enter", (WM_KEYDOWN, 0x0D, 0x1C, true), (WM_KEYUP, 0x0D, 0x1C, true));
            await Send("Pause", (WM_KEYDOWN, 0x13, 0x45, false), (WM_KEYUP, 0x13, 0x45, false));
        }
        Win32Properties.RemoveWndProcHookCallback(window, callback);
        Console.WriteLine($"[keys B] window focused element: {(window.FocusManager?.GetFocusedElement() as Control)?.GetType().Name ?? "none"}; window active: {window.IsActive}");
        foreach (var line in log) Console.WriteLine("  " + line);
        window.Close();
    }

    private static string Show(string? s) => s == null ? "null" : string.Concat(s.Select(ch => ch < 32 ? $"\\x{(int)ch:X2}" : ch.ToString()));
}
