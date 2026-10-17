using System.Runtime.InteropServices;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Input;

/// <summary>
/// A WH_KEYBOARD_LL hook that, while the session window is in the foreground, captures the
/// keys Windows would otherwise consume (Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4) and routes them
/// to the remote desktop. All other keys pass through to the ordinary window procedure.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private readonly Func<bool> _isActive;
    private readonly Action<int, int, bool, bool> _onKey; // vk, scancode, extended, down

    public LowLevelKeyboardHook(Func<bool> isActive, Action<int, int, bool, bool> onKey)
    {
        _isActive = isActive;
        _onKey = onKey;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isActive())
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected)
            {
                bool down = (info.flags & LLKHF_UP) == 0;
                bool extended = (info.flags & LLKHF_EXTENDED) != 0;
                bool altDown = (info.flags & LLKHF_ALTDOWN) != 0;
                int vk = (int)info.vkCode;
                bool ctrlDown = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

                bool capture = vk is VK_LWIN or VK_RWIN
                               || (altDown && vk is VK_TAB or VK_ESCAPE or VK_F4 or VK_SPACE)
                               || (ctrlDown && vk == VK_ESCAPE)
                               || (ctrlDown && altDown && vk == VK_DELETE);
                if (capture)
                {
                    _onKey(vk, (int)info.scanCode, extended, down);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
