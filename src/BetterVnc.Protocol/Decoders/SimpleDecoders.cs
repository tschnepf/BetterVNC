using System.Buffers.Binary;

namespace BetterVnc.Protocol.Decoders;

/// <summary>Raw (0): pixels in the client format, row by row.</summary>
public static class RawDecoder
{
    public static void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int rowBytes = rect.Width * Framebuffer.BytesPerPixel;
        if (rect.Right > fb.Width || rect.Bottom > fb.Height)
        {
            // Out-of-bounds rect: consume and drop so the stream stays in sync.
            r.Skip(rowBytes * rect.Height);
            return;
        }
        for (int y = 0; y < rect.Height; y++)
            r.ReadBytes(fb.Pixels.AsSpan(fb.Offset(rect.X, rect.Y + y), rowBytes));
    }
}

/// <summary>CopyRect (1): move a region of the framebuffer.</summary>
public static class CopyRectDecoder
{
    public static void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int srcX = r.ReadU16();
        int srcY = r.ReadU16();
        fb.CopyRect(srcX, srcY, rect);
    }
}

/// <summary>RRE (2): background colour plus a list of solid sub-rectangles.</summary>
public static class RreDecoder
{
    public static void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        uint count = r.ReadU32();
        uint bg = ReadPixel(r);
        fb.FillRect(rect, bg);
        for (uint i = 0; i < count; i++)
        {
            uint pixel = ReadPixel(r);
            int x = r.ReadU16();
            int y = r.ReadU16();
            int w = r.ReadU16();
            int h = r.ReadU16();
            fb.FillRect(new RfbRect(rect.X + x, rect.Y + y, w, h), pixel);
        }
    }

    internal static uint ReadPixel(RfbReader r)
    {
        Span<byte> px = stackalloc byte[4];
        r.ReadBytes(px);
        return BinaryPrimitives.ReadUInt32LittleEndian(px);
    }
}

/// <summary>Hextile (5): 16x16 tiles, each raw or background/foreground with sub-rectangles.</summary>
public static class HextileDecoder
{
    private const int RawFlag = 1;
    private const int BackgroundSpecified = 2;
    private const int ForegroundSpecified = 4;
    private const int AnySubrects = 8;
    private const int SubrectsColoured = 16;

    public static void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        uint bg = 0, fg = 0;
        for (int ty = rect.Y; ty < rect.Bottom; ty += 16)
        {
            int th = Math.Min(16, rect.Bottom - ty);
            for (int tx = rect.X; tx < rect.Right; tx += 16)
            {
                int tw = Math.Min(16, rect.Right - tx);
                var tile = new RfbRect(tx, ty, tw, th);
                int sub = r.ReadU8();
                if ((sub & RawFlag) != 0)
                {
                    RawDecoder.Decode(r, fb, tile);
                    continue;
                }
                if ((sub & BackgroundSpecified) != 0) bg = RreDecoder.ReadPixel(r);
                if ((sub & ForegroundSpecified) != 0) fg = RreDecoder.ReadPixel(r);
                fb.FillRect(tile, bg);
                if ((sub & AnySubrects) == 0) continue;
                int n = r.ReadU8();
                bool coloured = (sub & SubrectsColoured) != 0;
                for (int i = 0; i < n; i++)
                {
                    uint colour = coloured ? RreDecoder.ReadPixel(r) : fg;
                    int xy = r.ReadU8();
                    int wh = r.ReadU8();
                    int sx = xy >> 4, sy = xy & 0xF;
                    int sw = (wh >> 4) + 1, sh = (wh & 0xF) + 1;
                    fb.FillRect(new RfbRect(tx + sx, ty + sy, sw, sh), colour);
                }
            }
        }
    }
}

/// <summary>Zlib (6): a raw rectangle compressed with a per-connection zlib stream.</summary>
public sealed class ZlibDecoder
{
    private readonly ZlibInflater _inflater = new();

    public void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int len = (int)r.ReadU32();
        int outLen = rect.Width * rect.Height * Framebuffer.BytesPerPixel;
        var input = Pools.Rent(len);
        var output = Pools.Rent(outLen);
        try
        {
            r.ReadBytes(input.AsSpan(0, len));
            _inflater.Inflate(input, 0, len, output, 0, outLen);
            PixelCopy.CopyRows(output, rect.Width * Framebuffer.BytesPerPixel, fb, rect);
        }
        finally
        {
            Pools.Return(input);
            Pools.Return(output);
        }
    }
}

internal static class PixelCopy
{
    /// <summary>Copies tightly packed rows (<paramref name="srcRowBytes"/> each) into the framebuffer rectangle.</summary>
    public static void CopyRows(byte[] src, int srcRowBytes, Framebuffer fb, RfbRect rect)
    {
        var clipped = rect.Intersect(fb.Bounds);
        if (clipped.IsEmpty) return;
        int copyBytes = clipped.Width * Framebuffer.BytesPerPixel;
        int dx = (clipped.X - rect.X) * Framebuffer.BytesPerPixel;
        for (int y = 0; y < clipped.Height; y++)
        {
            int sy = clipped.Y - rect.Y + y;
            Buffer.BlockCopy(src, sy * srcRowBytes + dx, fb.Pixels, fb.Offset(clipped.X, clipped.Y + y), copyBytes);
        }
    }
}
