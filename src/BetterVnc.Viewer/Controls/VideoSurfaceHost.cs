using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using static BetterVnc.Viewer.Input.NativeMethods;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// The child window the picture is presented into by a flip-model swap chain (<see cref="SwapChainPresenter"/>).
/// Avalonia cannot draw over a native window (airspace) and places it over the control's whole bounding box, ignoring
/// any clip, so <see cref="VncView"/> keeps this control exactly the size of the visible picture and hides it while an
/// overlay must show. Mouse messages pass through it, and through the holder window Avalonia puts it in, to the
/// Avalonia window underneath (HTTRANSPARENT), so pointer input, the cursor and drag and drop work as if it were not
/// there; it never takes focus.
/// </summary>
internal sealed class VideoSurfaceHost : NativeControlHost
{
    private const string ClassName = "BetterVncPictureSurface";
    private static readonly WndProc s_childProc = ChildProc;
    private static readonly WndProc s_holderProc = HolderProc;
    private static readonly Dictionary<IntPtr, IntPtr> s_holders = new(); // holder window → its own window procedure; UI thread
    private static bool s_registered;

    private IntPtr _hwnd;

    public VideoSurfaceHost()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>The window was created (its handle) or is about to be destroyed (zero): attach or detach the swap chain.</summary>
    public event Action<IntPtr>? WindowChanged;

    public IntPtr Hwnd => _hwnd;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var instance = GetModuleHandle(null);
        if (!s_registered)
        {
            // No background brush: the swap chain owns every pixel.
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_childProc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                throw new Win32Exception();
            s_registered = true;
        }
        _hwnd = CreateWindowEx(WS_EX_NOPARENTNOTIFY, ClassName, "", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        WindowChanged?.Invoke(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        WindowChanged?.Invoke(IntPtr.Zero); // the swap chain lets go of the window first
        DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Moves the window to the control's bounds now (Avalonia would do it after the next render) and returns its client
    /// size in device pixels; zero while it has none. UI thread.
    /// </summary>
    public (int Width, int Height) Place()
    {
        TryUpdateNativeControlPosition();
        if (_hwnd == IntPtr.Zero || !IsEffectivelyVisible) return (0, 0);
        PassMouseThroughHolder();
        return GetClientRect(_hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);
    }

    /// <summary>
    /// Avalonia puts the child into a holder window of its own whose window procedure takes mouse messages; make that one
    /// transparent to the mouse too, so they reach the Avalonia window. Never the Avalonia window itself.
    /// </summary>
    private void PassMouseThroughHolder()
    {
        var holder = GetParent(_hwnd);
        if (holder == IntPtr.Zero || s_holders.ContainsKey(holder)) return;
        var top = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (holder == top || GetParent(holder) == IntPtr.Zero) return;
        var previous = SetWindowLongPtr(holder, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(s_holderProc));
        if (previous != IntPtr.Zero) s_holders[holder] = previous;
    }

    private static IntPtr ChildProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) => msg switch
    {
        WM_NCHITTEST => HTTRANSPARENT,
        WM_ERASEBKGND => 1,
        _ => DefWindowProc(hwnd, msg, wParam, lParam),
    };

    private static IntPtr HolderProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST) return HTTRANSPARENT;
        if (!s_holders.TryGetValue(hwnd, out var previous)) return DefWindowProc(hwnd, msg, wParam, lParam);
        if (msg == 0x0082 /* WM_NCDESTROY */) s_holders.Remove(hwnd);
        return CallWindowProc(previous, hwnd, msg, wParam, lParam);
    }
}
