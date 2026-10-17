namespace BetterVnc.Protocol;

/// <summary>A remote pointer image delivered via the Cursor pseudo-encoding; pixels are B,G,R,A (straight alpha).</summary>
public sealed class CursorShape
{
    public int Width { get; }
    public int Height { get; }
    public int HotX { get; }
    public int HotY { get; }
    public byte[] Bgra { get; }

    public bool IsEmpty => Width == 0 || Height == 0;

    public CursorShape(int width, int height, int hotX, int hotY, byte[] bgra)
    {
        Width = width;
        Height = height;
        HotX = hotX;
        HotY = hotY;
        Bgra = bgra;
    }

    public static CursorShape Empty { get; } = new(0, 0, 0, 0, Array.Empty<byte>());

    /// <summary>Parses the rich-cursor pseudo-encoding payload: pixels in client format followed by a 1-bit mask.</summary>
    public static CursorShape ReadRichCursor(RfbReader r, RfbRect rect)
    {
        int w = rect.Width, h = rect.Height;
        if (w == 0 || h == 0) return Empty;
        var pixels = r.ReadBytes(w * h * 4);
        int maskRow = (w + 7) / 8;
        var mask = r.ReadBytes(maskRow * h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool opaque = (mask[y * maskRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                pixels[(y * w + x) * 4 + 3] = opaque ? (byte)255 : (byte)0;
            }
        }
        return new CursorShape(w, h, rect.X, rect.Y, pixels);
    }
}
