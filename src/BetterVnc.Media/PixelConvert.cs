namespace BetterVnc.Media;

/// <summary>
/// Colour conversions between the BGRA the desktop is captured in and the NV12 the H.264 codecs speak.
/// BT.601 limited range, which is what a decoder assumes for an H.264 stream that carries no colour description.
/// Rows are processed in bands on the thread pool: a 2256×1504 desktop converts in about a millisecond.
/// </summary>
public static unsafe class PixelConvert
{
    /// <summary>
    /// Converts a BGRA (or BGRX) image to NV12. <paramref name="width"/> and <paramref name="height"/> must be even;
    /// the Y plane gets <paramref name="width"/>×<paramref name="height"/> bytes at <paramref name="yStride"/>, the
    /// interleaved UV plane half as many rows at <paramref name="uvStride"/>.
    /// </summary>
    public static void BgraToNv12(byte* src, int srcStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions.");
        int bands = Math.Clamp(height / 64, 1, Environment.ProcessorCount);
        int rowsPerBand = ((height / 2 + bands - 1) / bands) * 2;
        Parallel.For(0, bands, band =>
        {
            int first = band * rowsPerBand, last = Math.Min(height, first + rowsPerBand);
            for (int row = first; row < last; row += 2)
            {
                byte* s0 = src + (long)row * srcStride, s1 = s0 + srcStride;
                byte* d0 = y + (long)row * yStride, d1 = d0 + yStride;
                byte* duv = uv + (long)(row >> 1) * uvStride;
                for (int x = 0; x < width; x += 2)
                {
                    int b00 = s0[0], g00 = s0[1], r00 = s0[2];
                    int b01 = s0[4], g01 = s0[5], r01 = s0[6];
                    int b10 = s1[0], g10 = s1[1], r10 = s1[2];
                    int b11 = s1[4], g11 = s1[5], r11 = s1[6];
                    d0[0] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                    d0[1] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                    d1[0] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                    d1[1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);
                    int r = (r00 + r01 + r10 + r11 + 2) >> 2, g = (g00 + g01 + g10 + g11 + 2) >> 2, b = (b00 + b01 + b10 + b11 + 2) >> 2;
                    duv[0] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    duv[1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    s0 += 8; s1 += 8; d0 += 2; d1 += 2; duv += 2;
                }
            }
        });
    }

    /// <summary>Managed convenience overload: <paramref name="nv12"/> receives width*height*3/2 bytes, planes packed.</summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        if (nv12.Length < width * height * 3 / 2) throw new ArgumentException("NV12 buffer too small.");
        fixed (byte* s = bgra)
        fixed (byte* d = nv12)
            BgraToNv12(s, stride, width, height, d, width, d + width * height, width);
    }

    /// <summary>
    /// Converts NV12 to BGRX (alpha byte 255). Any width and height; the chroma sample of an odd last column or row
    /// is shared with its neighbour.
    /// </summary>
    public static void Nv12ToBgrx(byte* y, int yStride, byte* uv, int uvStride, int width, int height, byte* dst, int dstStride)
    {
        int bands = Math.Clamp(height / 64, 1, Environment.ProcessorCount);
        int rowsPerBand = ((height / 2 + bands) / bands) * 2;
        Parallel.For(0, bands, band =>
        {
            int first = band * rowsPerBand, last = Math.Min(height, first + rowsPerBand);
            for (int row = first; row < last; row++)
            {
                byte* sy = y + (long)row * yStride;
                byte* suv = uv + (long)(row >> 1) * uvStride;
                byte* d = dst + (long)row * dstStride;
                for (int x = 0; x < width; x++)
                {
                    int c = 298 * (sy[x] - 16) + 128;
                    int dd = suv[(x & ~1)] - 128, e = suv[(x & ~1) + 1] - 128;
                    int r = (c + 409 * e) >> 8, g = (c - 100 * dd - 208 * e) >> 8, b = (c + 516 * dd) >> 8;
                    d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    d[3] = 255;
                    d += 4;
                }
            }
        });
    }

    /// <summary>Managed convenience overload for packed NV12 (<paramref name="nv12"/> holds Y then UV at <paramref name="width"/> stride).</summary>
    public static void Nv12ToBgrx(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgrx)
    {
        if (bgrx.Length < width * height * 4) throw new ArgumentException("BGRX buffer too small.");
        fixed (byte* s = nv12)
        fixed (byte* d = bgrx)
            Nv12ToBgrx(s, width, s + width * height, width, width, height, d, width * 4);
    }
}
