using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BetterVnc.Protocol;
using Microsoft.Win32.SafeHandles;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Input;

/// <summary>Turns a remote <see cref="CursorShape"/> into a real Win32 cursor so the pointer tracks with zero latency.</summary>
public static class CursorFactory
{
    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    public static Cursor Create(CursorShape shape, double dpiScale)
    {
        if (shape.IsEmpty) return Cursors.None;

        int w = shape.Width, h = shape.Height;
        // Windows cursors use premultiplied alpha in the colour bitmap.
        var color = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte a = shape.Bgra[i * 4 + 3];
            color[i * 4 + 0] = (byte)(shape.Bgra[i * 4 + 0] * a / 255);
            color[i * 4 + 1] = (byte)(shape.Bgra[i * 4 + 1] * a / 255);
            color[i * 4 + 2] = (byte)(shape.Bgra[i * 4 + 2] * a / 255);
            color[i * 4 + 3] = a;
        }
        // AND mask: 1 = transparent. Rows padded to 16-bit boundaries for CreateBitmap.
        int maskStride = ((w + 15) / 16) * 2;
        var mask = new byte[maskStride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (shape.Bgra[(y * w + x) * 4 + 3] == 0)
                    mask[y * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

        IntPtr hColor = CreateBitmap(w, h, 1, 32, color);
        IntPtr hMask = CreateBitmap(w, h, 1, 1, mask);
        try
        {
            var info = new ICONINFO { fIcon = false, xHotspot = shape.HotX, yHotspot = shape.HotY, hbmMask = hMask, hbmColor = hColor };
            IntPtr hCursor = CreateIconIndirect(ref info);
            if (hCursor == IntPtr.Zero) return Cursors.Arrow;
            return CursorInteropHelper.Create(new SafeIconHandle(hCursor));
        }
        finally
        {
            DeleteObject(hColor);
            DeleteObject(hMask);
        }
    }
}
