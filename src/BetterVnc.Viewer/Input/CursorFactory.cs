using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BetterVnc.Protocol;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace BetterVnc.Viewer.Input;

/// <summary>Turns a remote <see cref="CursorShape"/> into a real OS cursor, so the pointer tracks with zero latency.</summary>
public static class CursorFactory
{
    /// <summary>A cursor for <paramref name="shape"/>: none for an empty shape, the arrow when the OS refuses the bitmap.</summary>
    public static Cursor Create(CursorShape shape)
    {
        if (shape.IsEmpty) return new Cursor(StandardCursorType.None);

        int w = shape.Width, h = shape.Height;
        // OS cursors take premultiplied alpha.
        var color = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte a = shape.Bgra[i * 4 + 3];
            color[i * 4 + 0] = (byte)(shape.Bgra[i * 4 + 0] * a / 255);
            color[i * 4 + 1] = (byte)(shape.Bgra[i * 4 + 1] * a / 255);
            color[i * 4 + 2] = (byte)(shape.Bgra[i * 4 + 2] * a / 255);
            color[i * 4 + 3] = a;
        }
        var handle = GCHandle.Alloc(color, GCHandleType.Pinned);
        try
        {
            // The bitmap copies the pixels; the cursor is made from it at once.
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), w * 4);
            return new Cursor(bitmap, new PixelPoint(Math.Clamp(shape.HotX, 0, w - 1), Math.Clamp(shape.HotY, 0, h - 1)));
        }
        catch (Exception)
        {
            return new Cursor(StandardCursorType.Arrow);
        }
        finally
        {
            handle.Free();
        }
    }
}
