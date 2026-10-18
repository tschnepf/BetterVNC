using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace BetterVnc.Media;

/// <summary>
/// Colour conversions between the BGRA the desktop is captured in and the NV12 the H.264 codecs speak.
/// BT.601 limited range, which is what a decoder assumes for an H.264 stream that carries no colour description.
/// Rows are processed in bands on the thread pool: a 2256×1504 desktop converts in about a millisecond.
/// </summary>
public static unsafe class PixelConvert
{
    /// <summary>
    /// Threads a BGRA→NV12 conversion uses at most by default: every logical processor but one, so the machine whose
    /// screen is streamed (often a VM with two or four vCPUs) keeps one for its own work.
    /// </summary>
    public static int DefaultThreads { get; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Below this many pixels a conversion runs on the calling thread; waking the pool would cost more than it saves.</summary>
    private const long ParallelMinPixels = 256 * 1024;

    /// <summary>
    /// Converts a BGRA (or BGRX) image to NV12. <paramref name="width"/> and <paramref name="height"/> must be even;
    /// the Y plane gets <paramref name="width"/>×<paramref name="height"/> bytes at <paramref name="yStride"/>, the
    /// interleaved UV plane half as many rows at <paramref name="uvStride"/>.
    /// </summary>
    public static void BgraToNv12(byte* src, int srcStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride)
        => BgraToNv12(src, srcStride, width, height, y, yStride, uv, uvStride, null, DefaultThreads, BestPath);

    /// <summary>
    /// Converts only <paramref name="regions"/> of a BGRA image (null: all of it) into an NV12 picture that already holds
    /// the rest, as when the screen changed in a few places. Each rectangle is widened to even coordinates, the 2×2
    /// blocks that share a chroma sample, and clipped to the picture. Uses up to <paramref name="maxThreads"/> threads
    /// for large regions; small ones convert on the calling thread. Same layout rules as the whole-picture overload.
    /// </summary>
    public static void BgraToNv12(byte* src, int srcStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride,
        IReadOnlyList<DirtyRect>? regions, int maxThreads)
        => BgraToNv12(src, srcStride, width, height, y, yStride, uv, uvStride, regions, maxThreads, BestPath);

    /// <summary>Managed convenience overload: <paramref name="nv12"/> receives width*height*3/2 bytes, planes packed.</summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        if (nv12.Length < width * height * 3 / 2) throw new ArgumentException("NV12 buffer too small.");
        fixed (byte* s = bgra)
        fixed (byte* d = nv12)
            BgraToNv12(s, stride, width, height, d, width, d + width * height, width);
    }

    /// <summary>The instruction set a BGRA→NV12 conversion runs on; every path gives the same bytes.</summary>
    internal enum SimdPath { Scalar, Vector128, Vector256 }

    /// <summary>256-bit vectors where the CPU has AVX2, else 128-bit (SSE, or Neon on ARM64), else plain code.</summary>
    internal static SimdPath BestPath { get; } =
        Vector256.IsHardwareAccelerated ? SimdPath.Vector256 : Vector128.IsHardwareAccelerated ? SimdPath.Vector128 : SimdPath.Scalar;

    /// <summary>What the BGRA→NV12 conversion runs on here, for logs.</summary>
    public static string BgraToNv12Instructions => BestPath switch
    {
        SimdPath.Vector256 => "256-bit SIMD",
        SimdPath.Vector128 => "128-bit SIMD",
        _ => "scalar code",
    };

    internal static void BgraToNv12(byte* src, int srcStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride,
        IReadOnlyList<DirtyRect>? regions, int maxThreads, SimdPath path)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions.");
        int count = regions?.Count ?? 1;
        long total = 0;
        for (int i = 0; i < count; i++) total += Block(regions, i, width, height).Area;
        if (total == 0) return;
        int threads = Math.Clamp(maxThreads, 1, 256);
        if (threads == 1 || total < ParallelMinPixels)
        {
            for (int i = 0; i < count; i++)
            {
                var b = Block(regions, i, width, height);
                if (b.Area > 0) ConvertBlock(src, srcStride, y, yStride, uv, uvStride, b.X, b.Right, b.Y, b.Bottom, path);
            }
            return;
        }
        // Bands of about equal size, two per thread, so a slow thread does not hold the others up.
        long bandPixels = Math.Max(32 * 1024, total / (threads * 2));
        var bands = new List<DirtyRect>();
        for (int i = 0; i < count; i++)
        {
            var b = Block(regions, i, width, height);
            if (b.Area == 0) continue;
            int rows = (int)Math.Clamp(bandPixels / b.Width, 2, b.Height) & ~1;
            for (int row = b.Y; row < b.Bottom; row += rows)
                bands.Add(new DirtyRect(b.X, row, b.Width, Math.Min(rows, b.Bottom - row)));
        }
        Parallel.For(0, bands.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var b = bands[i];
            ConvertBlock(src, srcStride, y, yStride, uv, uvStride, b.X, b.Right, b.Y, b.Bottom, path);
        });
    }

    /// <summary>Rectangle <paramref name="i"/> widened to whole 2×2 blocks and clipped to the picture (empty when outside).</summary>
    private static DirtyRect Block(IReadOnlyList<DirtyRect>? regions, int i, int width, int height)
    {
        if (regions == null) return new DirtyRect(0, 0, width, height);
        var r = regions[i];
        int x0 = Math.Max(0, r.X) & ~1, y0 = Math.Max(0, r.Y) & ~1;
        int x1 = (int)Math.Min(width, ((long)r.X + r.Width + 1) & ~1L), y1 = (int)Math.Min(height, ((long)r.Y + r.Height + 1) & ~1L);
        return x1 > x0 && y1 > y0 ? new DirtyRect(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>Columns [x0, x1) of rows [row0, row1); all four even.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // vector code is slow until the JIT recompiles it; start optimised
    private static void ConvertBlock(byte* src, int srcStride, byte* y, int yStride, byte* uv, int uvStride, int x0, int x1, int row0, int row1, SimdPath path)
    {
        int n = x1 - x0;
        for (int row = row0; row < row1; row += 2)
        {
            byte* s0 = src + (long)row * srcStride + (long)x0 * 4, s1 = s0 + srcStride;
            byte* d0 = y + (long)row * yStride + x0, d1 = d0 + yStride;
            byte* duv = uv + (long)(row >> 1) * uvStride + x0;
            int x = 0;
            if (path == SimdPath.Vector256)
                for (; x <= n - 16; x += 16) Convert16(s0 + x * 4, s1 + x * 4, d0 + x, d1 + x, duv + x);
            if (path >= SimdPath.Vector128)
                for (; x <= n - 8; x += 8) Convert8(s0 + x * 4, s1 + x * 4, d0 + x, d1 + x, duv + x);
            for (; x < n; x += 2) Convert2(s0 + x * 4, s1 + x * 4, d0 + x, d1 + x, duv + x);
        }
    }

    // The vector paths compute exactly what Convert2 does. Luma fits 16 bits unsigned (at most 220*255+128), so it is
    // done on 16 pixels at once in ushort lanes; chroma sums and products need signs and are done in int lanes.

    /// <summary>16 pixels of two rows: 16 Y bytes per row and 8 UV pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert16(byte* s0, byte* s1, byte* d0, byte* d1, byte* duv)
    {
        var m = Vector256.Create(0xFFu);
        Vector256<uint> a0 = Vector256.Load((uint*)s0), a1 = Vector256.Load((uint*)(s0 + 32));
        Vector256<uint> b0 = Vector256.Load((uint*)s1), b1 = Vector256.Load((uint*)(s1 + 32));
        var blue0 = Vector256.Narrow(a0 & m, a1 & m);
        var green0 = Vector256.Narrow(Vector256.ShiftRightLogical(a0, 8) & m, Vector256.ShiftRightLogical(a1, 8) & m);
        var red0 = Vector256.Narrow(Vector256.ShiftRightLogical(a0, 16) & m, Vector256.ShiftRightLogical(a1, 16) & m);
        var blue1 = Vector256.Narrow(b0 & m, b1 & m);
        var green1 = Vector256.Narrow(Vector256.ShiftRightLogical(b0, 8) & m, Vector256.ShiftRightLogical(b1, 8) & m);
        var red1 = Vector256.Narrow(Vector256.ShiftRightLogical(b0, 16) & m, Vector256.ShiftRightLogical(b1, 16) & m);
        var luma = Vector256.Narrow(Luma(red0, green0, blue0), Luma(red1, green1, blue1));
        luma.GetLower().Store(d0);
        luma.GetUpper().Store(d1);
        var b = Average(blue0 + blue1);
        var g = Average(green0 + green1);
        var r = Average(red0 + red1);
        var uv = Chroma(r, g, b);
        Vector256.Narrow(uv, Vector256<uint>.Zero).GetLower().AsByte().Store(duv);
    }

    private static Vector256<ushort> Luma(Vector256<ushort> r, Vector256<ushort> g, Vector256<ushort> b) =>
        Vector256.ShiftRightLogical(r * (ushort)66 + g * (ushort)129 + b * (ushort)25 + Vector256.Create((ushort)128), 8) + Vector256.Create((ushort)16);

    /// <summary>Column sums of two rows in, the rounded mean of each horizontal pair (2×2 block) out.</summary>
    private static Vector256<int> Average(Vector256<ushort> columnSums)
    {
        var pairs = columnSums.AsUInt32();
        return Vector256.ShiftRightLogical((pairs & Vector256.Create(0xFFFFu)) + Vector256.ShiftRightLogical(pairs, 16) + Vector256.Create(2u), 2).AsInt32();
    }

    /// <summary>U in the low byte, V in the next, of each lane.</summary>
    private static Vector256<uint> Chroma(Vector256<int> r, Vector256<int> g, Vector256<int> b)
    {
        var round = Vector256.Create(128);
        var u = Vector256.ShiftRightArithmetic(b * 112 - g * 74 - r * 38 + round, 8) + round;
        var v = Vector256.ShiftRightArithmetic(r * 112 - g * 94 - b * 18 + round, 8) + round;
        return (u | Vector256.ShiftLeft(v, 8)).AsUInt32();
    }

    /// <summary>8 pixels of two rows: 8 Y bytes per row and 4 UV pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert8(byte* s0, byte* s1, byte* d0, byte* d1, byte* duv)
    {
        var m = Vector128.Create(0xFFu);
        Vector128<uint> a0 = Vector128.Load((uint*)s0), a1 = Vector128.Load((uint*)(s0 + 16));
        Vector128<uint> b0 = Vector128.Load((uint*)s1), b1 = Vector128.Load((uint*)(s1 + 16));
        var blue0 = Vector128.Narrow(a0 & m, a1 & m);
        var green0 = Vector128.Narrow(Vector128.ShiftRightLogical(a0, 8) & m, Vector128.ShiftRightLogical(a1, 8) & m);
        var red0 = Vector128.Narrow(Vector128.ShiftRightLogical(a0, 16) & m, Vector128.ShiftRightLogical(a1, 16) & m);
        var blue1 = Vector128.Narrow(b0 & m, b1 & m);
        var green1 = Vector128.Narrow(Vector128.ShiftRightLogical(b0, 8) & m, Vector128.ShiftRightLogical(b1, 8) & m);
        var red1 = Vector128.Narrow(Vector128.ShiftRightLogical(b0, 16) & m, Vector128.ShiftRightLogical(b1, 16) & m);
        var luma = Vector128.Narrow(Luma(red0, green0, blue0), Luma(red1, green1, blue1)).AsUInt64();
        Unsafe.WriteUnaligned(d0, luma.GetElement(0));
        Unsafe.WriteUnaligned(d1, luma.GetElement(1));
        var b = Average(blue0 + blue1);
        var g = Average(green0 + green1);
        var r = Average(red0 + red1);
        var uv = Chroma(r, g, b);
        Unsafe.WriteUnaligned(duv, Vector128.Narrow(uv, Vector128<uint>.Zero).AsUInt64().GetElement(0));
    }

    private static Vector128<ushort> Luma(Vector128<ushort> r, Vector128<ushort> g, Vector128<ushort> b) =>
        Vector128.ShiftRightLogical(r * (ushort)66 + g * (ushort)129 + b * (ushort)25 + Vector128.Create((ushort)128), 8) + Vector128.Create((ushort)16);

    private static Vector128<int> Average(Vector128<ushort> columnSums)
    {
        var pairs = columnSums.AsUInt32();
        return Vector128.ShiftRightLogical((pairs & Vector128.Create(0xFFFFu)) + Vector128.ShiftRightLogical(pairs, 16) + Vector128.Create(2u), 2).AsInt32();
    }

    private static Vector128<uint> Chroma(Vector128<int> r, Vector128<int> g, Vector128<int> b)
    {
        var round = Vector128.Create(128);
        var u = Vector128.ShiftRightArithmetic(b * 112 - g * 74 - r * 38 + round, 8) + round;
        var v = Vector128.ShiftRightArithmetic(r * 112 - g * 94 - b * 18 + round, 8) + round;
        return (u | Vector128.ShiftLeft(v, 8)).AsUInt32();
    }

    /// <summary>One 2×2 block: two Y bytes per row and one UV pair.</summary>
    private static void Convert2(byte* s0, byte* s1, byte* d0, byte* d1, byte* duv)
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
