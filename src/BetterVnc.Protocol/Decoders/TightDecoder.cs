namespace BetterVnc.Protocol.Decoders;

/// <summary>
/// Tight (7) decoder for the 32bpp/depth-24 client format, where Tight uses 3-byte "TPIXEL"
/// values in R,G,B order. Supports Fill, JPEG and Basic (copy / palette / gradient filters)
/// over four persistent zlib streams.
/// </summary>
public sealed class TightDecoder
{
    private const int MinBytesToCompress = 12;
    private readonly ZlibInflater[] _streams = { new(), new(), new(), new() };
    private readonly IJpegDecoder? _jpeg;

    public TightDecoder(IJpegDecoder? jpegDecoder)
    {
        _jpeg = jpegDecoder;
    }

    public void Decode(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int ctl = r.ReadU8();
        for (int i = 0; i < 4; i++)
            if ((ctl & (1 << i)) != 0) _streams[i].Reset();
        ctl >>= 4;

        if (ctl == 0x08)
        {
            fb.FillRect(rect, ReadTPixel(r));
            return;
        }
        if (ctl == 0x09)
        {
            DecodeJpeg(r, fb, rect);
            return;
        }
        if (ctl == 0x0A)
            throw new VncException("Tight PNG sub-encoding is not supported (not advertised)");
        if ((ctl & 0x08) != 0)
            throw new VncException($"Unknown Tight compression control 0x{ctl:X}");

        int streamId = ctl & 0x03;
        int filter = (ctl & 0x04) != 0 ? r.ReadU8() : 0;

        int rowBytes;
        byte[]? palette = null;
        int numColors = 0;
        switch (filter)
        {
            case 0: // copy
            case 2: // gradient
                rowBytes = rect.Width * 3;
                break;
            case 1: // palette
                numColors = r.ReadU8() + 1;
                palette = new byte[numColors * 4];
                Span<byte> t = stackalloc byte[3];
                for (int i = 0; i < numColors; i++)
                {
                    r.ReadBytes(t);
                    palette[i * 4 + 0] = t[2];
                    palette[i * 4 + 1] = t[1];
                    palette[i * 4 + 2] = t[0];
                }
                rowBytes = numColors == 2 ? (rect.Width + 7) / 8 : rect.Width;
                break;
            default:
                throw new VncException($"Unknown Tight filter {filter}");
        }

        int dataLen = rowBytes * rect.Height;
        var data = Pools.Rent(dataLen);
        try
        {
            if (dataLen < MinBytesToCompress)
            {
                r.ReadBytes(data.AsSpan(0, dataLen));
            }
            else
            {
                int clen = ReadCompactLength(r);
                var compressed = Pools.Rent(clen);
                try
                {
                    r.ReadBytes(compressed.AsSpan(0, clen));
                    _streams[streamId].Inflate(compressed, 0, clen, data, 0, dataLen);
                }
                finally
                {
                    Pools.Return(compressed);
                }
            }

            switch (filter)
            {
                case 0:
                    WriteTPixels(data, fb, rect);
                    break;
                case 1:
                    WritePalette(data, palette!, numColors, fb, rect);
                    break;
                case 2:
                    WriteGradient(data, fb, rect);
                    break;
            }
        }
        finally
        {
            Pools.Return(data);
        }
    }

    private void DecodeJpeg(RfbReader r, Framebuffer fb, RfbRect rect)
    {
        int len = ReadCompactLength(r);
        var jpeg = Pools.Rent(len);
        try
        {
            r.ReadBytes(jpeg.AsSpan(0, len));
            if (_jpeg == null)
            {
                fb.FillRect(rect, 0x00FF00FF); // magenta: visible marker for a missing decoder
                return;
            }
            var bgrx = _jpeg.DecodeToBgrx(jpeg.AsSpan(0, len), rect.Width, rect.Height);
            PixelCopy.CopyRows(bgrx, rect.Width * 4, fb, rect);
        }
        finally
        {
            Pools.Return(jpeg);
        }
    }

    internal static int ReadCompactLength(RfbReader r)
    {
        int b = r.ReadU8();
        int len = b & 0x7F;
        if ((b & 0x80) != 0)
        {
            b = r.ReadU8();
            len |= (b & 0x7F) << 7;
            if ((b & 0x80) != 0)
            {
                b = r.ReadU8();
                len |= b << 14;
            }
        }
        return len;
    }

    private static uint ReadTPixel(RfbReader r)
    {
        Span<byte> t = stackalloc byte[3];
        r.ReadBytes(t);
        return (uint)(t[0] << 16 | t[1] << 8 | t[2]);
    }

    private static void WriteTPixels(byte[] data, Framebuffer fb, RfbRect rect)
    {
        var clipped = rect.Intersect(fb.Bounds);
        if (clipped.IsEmpty) return;
        int srcRow = rect.Width * 3;
        for (int y = 0; y < clipped.Height; y++)
        {
            int sy = clipped.Y - rect.Y + y;
            int si = sy * srcRow + (clipped.X - rect.X) * 3;
            int di = fb.Offset(clipped.X, clipped.Y + y);
            var px = fb.Pixels;
            for (int x = 0; x < clipped.Width; x++)
            {
                px[di + 0] = data[si + 2];
                px[di + 1] = data[si + 1];
                px[di + 2] = data[si + 0];
                px[di + 3] = 0;
                si += 3;
                di += 4;
            }
        }
    }

    private static void WritePalette(byte[] data, byte[] palette, int numColors, Framebuffer fb, RfbRect rect)
    {
        var clipped = rect.Intersect(fb.Bounds);
        if (clipped.IsEmpty) return;
        var px = fb.Pixels;
        if (numColors == 2)
        {
            int rowBytes = (rect.Width + 7) / 8;
            for (int y = 0; y < clipped.Height; y++)
            {
                int sy = clipped.Y - rect.Y + y;
                int di = fb.Offset(clipped.X, clipped.Y + y);
                for (int x = 0; x < clipped.Width; x++)
                {
                    int sx = clipped.X - rect.X + x;
                    int bit = (data[sy * rowBytes + (sx >> 3)] >> (7 - (sx & 7))) & 1;
                    Buffer.BlockCopy(palette, bit * 4, px, di, 4);
                    di += 4;
                }
            }
        }
        else
        {
            for (int y = 0; y < clipped.Height; y++)
            {
                int sy = clipped.Y - rect.Y + y;
                int si = sy * rect.Width + (clipped.X - rect.X);
                int di = fb.Offset(clipped.X, clipped.Y + y);
                for (int x = 0; x < clipped.Width; x++)
                {
                    int idx = data[si++];
                    if (idx >= numColors) idx = 0;
                    Buffer.BlockCopy(palette, idx * 4, px, di, 4);
                    di += 4;
                }
            }
        }
    }

    private static void WriteGradient(byte[] data, Framebuffer fb, RfbRect rect)
    {
        int w = rect.Width;
        var prevRow = new byte[w * 3];
        var thisRow = new byte[w * 3];
        var px = fb.Pixels;
        for (int y = 0; y < rect.Height; y++)
        {
            int si = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int left = x > 0 ? thisRow[(x - 1) * 3 + c] : 0;
                    int above = prevRow[x * 3 + c];
                    int aboveLeft = x > 0 ? prevRow[(x - 1) * 3 + c] : 0;
                    int pred = Math.Clamp(left + above - aboveLeft, 0, 255);
                    thisRow[x * 3 + c] = (byte)(data[si + x * 3 + c] + pred);
                }
            }
            int fy = rect.Y + y;
            if (fy >= 0 && fy < fb.Height)
            {
                for (int x = 0; x < w; x++)
                {
                    int fx = rect.X + x;
                    if (fx < 0 || fx >= fb.Width) continue;
                    int di = fb.Offset(fx, fy);
                    px[di + 0] = thisRow[x * 3 + 2];
                    px[di + 1] = thisRow[x * 3 + 1];
                    px[di + 2] = thisRow[x * 3 + 0];
                    px[di + 3] = 0;
                }
            }
            (prevRow, thisRow) = (thisRow, prevRow);
        }
    }
}
