using System.Runtime.InteropServices;

namespace BetterVnc.Media;

/// <summary>
/// The pointer Windows shows right now, through GetCursorInfo and the cursor's bitmaps: for capture without Desktop
/// Duplication (GDI), and for the shape at the start of a capture, before the compositor reports a change. Polling;
/// the shape is read again only when the cursor handle changes. Use from one thread.
/// </summary>
public sealed unsafe class CursorReader
{
    private IntPtr _handle;
    private bool _visible;
    private bool _reported;

    /// <summary>
    /// True when the pointer changed since the last call (always on the first): whether it shows now, and its shape when
    /// that changed (null when only the visibility did, or the shape could not be read).
    /// </summary>
    public bool Poll(out bool visible, out PointerShape? shape)
    {
        shape = null;
        var info = new CURSORINFO { cbSize = sizeof(CURSORINFO) };
        if (!GetCursorInfo(ref info))
        {
            visible = _visible;
            return false; // the secure desktop, for one: keep what we had
        }
        visible = (info.flags & CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero;
        bool changed = !_reported || visible != _visible;
        if (visible && info.hCursor != _handle)
        {
            _handle = info.hCursor;
            shape = Read(info.hCursor);
            changed |= shape != null;
        }
        _visible = visible;
        _reported = true;
        return changed;
    }

    /// <summary>The image of a cursor handle; null when it cannot be read.</summary>
    public static PointerShape? Read(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var ii)) return null;
        IntPtr dc = IntPtr.Zero;
        try
        {
            BITMAP bm;
            if (GetObject(ii.hbmMask, sizeof(BITMAP), &bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
            int width = bm.bmWidth;
            bool mono = ii.hbmColor == IntPtr.Zero;
            int height = mono ? bm.bmHeight / 2 : bm.bmHeight;
            if (height <= 0 || width > 1024 || height > 1024) return null;
            dc = GetDC(IntPtr.Zero);

            // The AND mask (and for a monochrome cursor the XOR mask below it) as a top-down 1-bit DIB.
            int maskPitch = (width + 31) / 32 * 4;
            int maskRows = mono ? height * 2 : height;
            var mask = new byte[maskPitch * maskRows];
            if (!ReadBits(dc, ii.hbmMask, width, maskRows, 1, mask)) return null;
            if (mono) return PointerShape.FromMonochrome(mask, width, height, maskPitch, ii.xHotspot, ii.yHotspot);

            var color = new byte[width * height * 4];
            if (!ReadBits(dc, ii.hbmColor, width, height, 32, color)) return null;
            bool alpha = false;
            for (int i = 3; i < color.Length && !alpha; i += 4) alpha = color[i] != 0;
            return alpha
                ? PointerShape.FromColor(color, width, height, width * 4, ii.xHotspot, ii.yHotspot)
                : PointerShape.FromColorAndMask(color, width * 4, mask, maskPitch, width, height, ii.xHotspot, ii.yHotspot);
        }
        catch (ArgumentException) { return null; }
        finally
        {
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    private static bool ReadBits(IntPtr dc, IntPtr bitmap, int width, int rows, ushort bitCount, byte[] into)
    {
        // A BITMAPINFOHEADER plus room for a two-entry colour table (the 1-bit case writes one).
        var info = stackalloc byte[sizeof(BITMAPINFOHEADER) + 2 * 4];
        var header = (BITMAPINFOHEADER*)info;
        *header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -rows, // top-down
            biPlanes = 1,
            biBitCount = bitCount,
        };
        fixed (byte* p = into) return GetDIBits(dc, bitmap, 0, (uint)rows, p, info, 0) == rows;
    }

    private const uint CURSOR_SHOWING = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hCursor;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, void* buffer);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, void* bits, void* info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
