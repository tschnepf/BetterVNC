namespace BetterVnc.Protocol.Decoders;

/// <summary>
/// ZRLE (16) decoder for the 32bpp/depth-24 little-endian client format. In that format the
/// "CPIXEL" is the three least significant bytes of the pixel, i.e. B,G,R in memory order.
/// One zlib stream persists for the connection; each rectangle is a flushed chunk of it.
/// </summary>
public sealed class ZrleDecoder
{
    private readonly ZlibInflater _inflater = new();

    public void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int len = (int)r.ReadU32();
        var input = Pools.Rent(len);
        try
        {
            r.ReadBytes(input.AsSpan(0, len));
            var data = _inflater.InflateAll(input, 0, len, rect.Width * rect.Height * 3 + 1024);
            var cursor = new ByteCursor(data);
            DecodeTiles(ref cursor, fb, rect);
        }
        finally
        {
            Pools.Return(input);
        }
    }

    private static void DecodeTiles(ref ByteCursor c, Framebuffer fb, RfbRect rect)
    {
        var tileBuf = new uint[64 * 64];
        var palette = new uint[128];
        for (int ty = rect.Y; ty < rect.Bottom; ty += 64)
        {
            int th = Math.Min(64, rect.Bottom - ty);
            for (int tx = rect.X; tx < rect.Right; tx += 64)
            {
                int tw = Math.Min(64, rect.Right - tx);
                int n = tw * th;
                int sub = c.ReadU8();

                if (sub == 0)
                {
                    for (int i = 0; i < n; i++) tileBuf[i] = ReadCPixel(ref c);
                }
                else if (sub == 1)
                {
                    uint solid = ReadCPixel(ref c);
                    fb.FillRect(new RfbRect(tx, ty, tw, th), solid);
                    continue;
                }
                else if (sub <= 16)
                {
                    int paletteSize = sub;
                    for (int i = 0; i < paletteSize; i++) palette[i] = ReadCPixel(ref c);
                    int bits = paletteSize == 2 ? 1 : paletteSize <= 4 ? 2 : 4;
                    int rowBytes = (tw * bits + 7) / 8;
                    for (int y = 0; y < th; y++)
                    {
                        var row = c.ReadSpan(rowBytes);
                        int bitPos = 0;
                        for (int x = 0; x < tw; x++)
                        {
                            int byteIdx = bitPos >> 3;
                            int shift = 8 - bits - (bitPos & 7);
                            int idx = (row[byteIdx] >> shift) & ((1 << bits) - 1);
                            tileBuf[y * tw + x] = palette[idx];
                            bitPos += bits;
                        }
                    }
                }
                else if (sub == 128)
                {
                    int pos = 0;
                    while (pos < n)
                    {
                        uint pixel = ReadCPixel(ref c);
                        int run = ReadRunLength(ref c);
                        if (pos + run > n) throw new VncException("ZRLE run exceeds tile");
                        for (int i = 0; i < run; i++) tileBuf[pos++] = pixel;
                    }
                }
                else if (sub >= 130)
                {
                    int paletteSize = sub - 128;
                    for (int i = 0; i < paletteSize; i++) palette[i] = ReadCPixel(ref c);
                    int pos = 0;
                    while (pos < n)
                    {
                        int idx = c.ReadU8();
                        int run = 1;
                        if ((idx & 0x80) != 0)
                        {
                            idx &= 0x7F;
                            run = ReadRunLength(ref c);
                        }
                        if (pos + run > n) throw new VncException("ZRLE run exceeds tile");
                        uint pixel = palette[idx];
                        for (int i = 0; i < run; i++) tileBuf[pos++] = pixel;
                    }
                }
                else
                {
                    throw new VncException($"Invalid ZRLE sub-encoding {sub}");
                }

                WriteTile(tileBuf, fb, tx, ty, tw, th);
            }
        }
    }

    private static int ReadRunLength(ref ByteCursor c)
    {
        int len = 1;
        int b;
        do
        {
            b = c.ReadU8();
            len += b;
        } while (b == 255);
        return len;
    }

    private static uint ReadCPixel(ref ByteCursor c)
    {
        var s = c.ReadSpan(3);
        return (uint)(s[0] | s[1] << 8 | s[2] << 16);
    }

    private static void WriteTile(uint[] tile, Framebuffer fb, int tx, int ty, int tw, int th)
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(fb.Pixels.AsSpan());
        for (int y = 0; y < th; y++)
        {
            int fy = ty + y;
            if (fy < 0 || fy >= fb.Height) continue;
            int x0 = Math.Max(0, tx);
            int x1 = Math.Min(fb.Width, tx + tw);
            if (x1 <= x0) continue;
            tile.AsSpan(y * tw + (x0 - tx), x1 - x0).CopyTo(span.Slice(fy * fb.Width + x0));
        }
    }
}
