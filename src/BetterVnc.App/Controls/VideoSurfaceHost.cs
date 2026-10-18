using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BetterVnc.Media;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Controls;

/// <summary>
/// The child window the agent stream is presented into by a flip-model swap chain (<see cref="VideoRenderer"/>). WPF
/// cannot draw over a child window (airspace), so <see cref="VncView"/> places it itself: exactly over the visible part
/// of the streamed picture, inside the scroll viewport, and at zero size while the RFB picture or a WPF overlay must
/// show. HwndHost would otherwise stretch it over the whole element and ignore the scroll viewport's clip. Mouse
/// messages pass through it to the view underneath (HTTRANSPARENT), so input and the remote cursor work as if it were
/// not there; it never takes focus.
/// </summary>
internal sealed class VideoSurfaceHost : HwndHost
{
    private const string ClassName = "BetterVncVideoSurface";
    private static bool s_registered;

    private readonly Action _placementRequested;
    private IntPtr _hwnd;

    /// <param name="placementRequested">Called whenever WPF lays the element out again (resize, scroll, DPI change); the owner then calls <see cref="Place"/>.</param>
    public VideoSurfaceHost(Action placementRequested)
    {
        _placementRequested = placementRequested;
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>The window was created (its handle) or is about to be destroyed (zero): attach or detach the swap chain.</summary>
    public event Action<IntPtr>? WindowChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var instance = GetModuleHandle(null);
        if (!s_registered)
        {
            // No background brush and the default window procedure: the swap chain owns every pixel, HwndHost subclasses it.
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                throw new Win32Exception();
            s_registered = true;
        }
        _hwnd = CreateWindowEx(WS_EX_NOPARENTNOTIFY, ClassName, "", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        WindowChanged?.Invoke(_hwnd);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        WindowChanged?.Invoke(IntPtr.Zero); // the swap chain lets go of the window first
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return HTTRANSPARENT;
            case WM_ERASEBKGND:
                handled = true;
                return 1;
            case WM_WINDOWPOSCHANGING:
                // HwndHost's own handler pins the window to the element's layout rectangle on every move, which would
                // undo Place (the scroll-viewport clip, the zero size while hidden). The owner is the only one moving it.
                handled = true;
                return 0;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>HwndHost wants the window over the element's bounding box; the owner decides instead.</summary>
    protected override void OnWindowPositionChanged(Rect rcBoundingBox) => _placementRequested();

    /// <summary>Moves the window (device pixels in the parent's client area); an empty placement shrinks it to nothing.</summary>
    public void Place(VideoPlacement placement)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, IntPtr.Zero, placement.X, placement.Y, Math.Max(0, placement.Width), Math.Max(0, placement.Height),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}
