using System.Buffers.Binary;

namespace BetterVnc.Media;

/// <summary>
/// A pointer image for a viewer that draws the pointer itself: <see cref="Width"/>×<see cref="Height"/> pixels of
/// B, G, R, A with straight alpha, top row first, and the hot spot. Windows also has pointers that invert the screen
/// under them (the text I-beam is one), which an image with alpha cannot express: those pixels become black with a
/// white outline around them, so the shape still shows on light and on dark backgrounds.
/// </summary>
public sealed class PointerShape
{
    private const uint Transparent = 0, Black = 0xFF000000, White = 0xFFFFFFFF;

    public PointerShape(int width, int height, int hotX, int hotY, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) throw new ArgumentException("The pixels do not match the size.");
        Width = width;
        Height = height;
        HotX = Math.Clamp(hotX, 0, width - 1);
        HotY = Math.Clamp(hotY, 0, height - 1);
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public int HotX { get; }
    public int HotY { get; }
    public byte[] Bgra { get; }

    /// <summary>32-bit pixels with an alpha channel (a colour pointer from Desktop Duplication, or an alpha cursor from GDI).</summary>
    public static PointerShape FromColor(ReadOnlySpan<byte> pixels, int width, int height, int pitch, int hotX, int hotY)
    {
        Check(pixels, width * 4, height, pitch);
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++) pixels.Slice(y * pitch, width * 4).CopyTo(bgra.AsSpan(y * width * 4));
        return new PointerShape(width, height, hotX, hotY, bgra);
    }

    /// <summary>
    /// 32-bit pixels whose alpha byte is a mask (Desktop Duplication's masked colour type): 0 draws the colour, 0xFF
    /// XORs it with the screen, which leaves the screen as it is for black and inverts it for anything else.
    /// </summary>
    public static PointerShape FromMaskedColor(ReadOnlySpan<byte> pixels, int width, int height, int pitch, int hotX, int hotY)
    {
        Check(pixels, width * 4, height, pitch);
        var px = new uint[width * height];
        bool[]? inverted = null;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint p = BinaryPrimitives.ReadUInt32LittleEndian(pixels.Slice(y * pitch + x * 4, 4));
                uint rgb = p & 0x00FFFFFF;
                if (p >> 24 == 0) px[y * width + x] = Black | rgb;
                else if (rgb == 0) px[y * width + x] = Transparent;
                else Invert(px, ref inverted, y * width + x);
            }
        }
        return Finish(px, inverted, width, height, hotX, hotY);
    }

    /// <summary>
    /// A monochrome pointer: a 1-bit AND mask over a 1-bit XOR mask of the same size, so <paramref name="planes"/> holds
    /// 2×<paramref name="height"/> rows of <paramref name="pitch"/> bytes (Desktop Duplication's monochrome type, or a
    /// GDI cursor without a colour bitmap).
    /// </summary>
    public static PointerShape FromMonochrome(ReadOnlySpan<byte> planes, int width, int height, int pitch, int hotX, int hotY)
    {
        Check(planes, (width + 7) / 8, height * 2, pitch);
        var px = new uint[width * height];
        bool[]? inverted = null;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool and = Bit(planes, pitch, x, y), xor = Bit(planes, pitch, x, y + height);
                int i = y * width + x;
                //  AND XOR   Windows draws      here
                //   0   0    black              black
                //   0   1    white              white
                //   1   0    the screen         transparent
                //   1   1    the screen inverted   black with a white outline
                if (!and) px[i] = xor ? White : Black;
                else if (!xor) px[i] = Transparent;
                else Invert(px, ref inverted, i);
            }
        }
        return Finish(px, inverted, width, height, hotX, hotY);
    }

    /// <summary>
    /// A GDI cursor with a colour bitmap but no alpha channel: where the 1-bit AND mask is 0 the colour is drawn, where
    /// it is 1 the colour is XORed with the screen (nothing for black, an inversion for anything else).
    /// </summary>
    public static PointerShape FromColorAndMask(ReadOnlySpan<byte> color, int colorPitch, ReadOnlySpan<byte> mask, int maskPitch, int width, int height, int hotX, int hotY)
    {
        Check(color, width * 4, height, colorPitch);
        Check(mask, (width + 7) / 8, height, maskPitch);
        var px = new uint[width * height];
        bool[]? inverted = null;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint rgb = BinaryPrimitives.ReadUInt32LittleEndian(color.Slice(y * colorPitch + x * 4, 4)) & 0x00FFFFFF;
                int i = y * width + x;
                if (!Bit(mask, maskPitch, x, y)) px[i] = Black | rgb;
                else if (rgb == 0) px[i] = Transparent;
                else Invert(px, ref inverted, i);
            }
        }
        return Finish(px, inverted, width, height, hotX, hotY);
    }

    private static void Check(ReadOnlySpan<byte> data, int rowBytes, int rows, int pitch)
    {
        if (rows <= 0 || rowBytes <= 0 || pitch < rowBytes || data.Length < (long)pitch * (rows - 1) + rowBytes)
            throw new ArgumentException("The pointer data is smaller than its size says.");
    }

    private static bool Bit(ReadOnlySpan<byte> plane, int pitch, int x, int y) => (plane[y * pitch + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    private static void Invert(uint[] px, ref bool[]? inverted, int i)
    {
        px[i] = Black;
        (inverted ??= new bool[px.Length])[i] = true;
    }

    private static PointerShape Finish(uint[] px, bool[]? inverted, int width, int height, int hotX, int hotY)
    {
        if (inverted != null)
        {
            // A white edge on the transparent pixels next to the inverted ones (left, right, above, below). Only inverted
            // pixels count, so the new white pixels do not grow the edge further.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (px[i] != Transparent) continue;
                    if ((x > 0 && inverted[i - 1]) || (x < width - 1 && inverted[i + 1]) || (y > 0 && inverted[i - width]) || (y < height - 1 && inverted[i + width]))
                        px[i] = White;
                }
            }
        }
        var bgra = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bgra.AsSpan(i * 4), px[i]);
        return new PointerShape(width, height, hotX, hotY, bgra);
    }
}
