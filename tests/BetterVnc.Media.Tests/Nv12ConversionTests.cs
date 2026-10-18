using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

/// <summary>The vectorised BGRA→NV12 paths, and the region overload, must give exactly the bytes the scalar code gave.</summary>
public unsafe class Nv12ConversionTests
{
    /// <summary>The conversion as it was before vectorising (5f997f1), single-threaded: the reference every path must match.</summary>
    private static void Reference(byte* src, int srcStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride)
    {
        for (int row = 0; row < height; row += 2)
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
    }

    public static IEnumerable<object[]> Paths()
    {
        yield return new object[] { (int)PixelConvert.SimdPath.Scalar };
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) yield return new object[] { (int)PixelConvert.SimdPath.Vector128 };
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated) yield return new object[] { (int)PixelConvert.SimdPath.Vector256 };
    }

    /// <summary>Random pixels, alpha included (it must be ignored), with stretches of the extremes that stress the sums.</summary>
    private static byte[] Image(int width, int height, int stride, int seed)
    {
        var rnd = new Random(seed);
        var img = new byte[stride * height];
        rnd.NextBytes(img);
        for (int row = 0; row < height; row++)
            for (int x = 0; x < width; x++)
            {
                int i = row * stride + x * 4;
                switch ((x / 5 + row / 3 + seed) % 7)
                {
                    case 0: img[i] = img[i + 1] = img[i + 2] = 0; break;
                    case 1: img[i] = img[i + 1] = img[i + 2] = 255; break;
                    case 2: img[i] = 255; img[i + 1] = 0; img[i + 2] = 255; break; // magenta: largest U and V
                    case 3: img[i] = 0; img[i + 1] = 255; img[i + 2] = 0; break;   // green: smallest
                }
            }
        return img;
    }

    private sealed class Nv12
    {
        public readonly int YStride, UvStride, Height;
        public readonly byte[] Y, Uv;
        public Nv12(int width, int height, int padding)
        {
            YStride = width + padding;
            UvStride = width + padding;
            Height = height;
            Y = new byte[YStride * height];
            Uv = new byte[UvStride * height / 2];
            Array.Fill(Y, (byte)0xCD);
            Array.Fill(Uv, (byte)0xCD);
        }
    }

    private static void Run(byte[] src, int srcStride, int width, int height, Nv12 dst, IReadOnlyList<DirtyRect>? regions, int threads, PixelConvert.SimdPath path)
    {
        fixed (byte* s = src) fixed (byte* y = dst.Y) fixed (byte* uv = dst.Uv)
            PixelConvert.BgraToNv12(s, srcStride, width, height, y, dst.YStride, uv, dst.UvStride, regions, threads, path);
    }

    private static Nv12 RunReference(byte[] src, int srcStride, int width, int height, int padding)
    {
        var dst = new Nv12(width, height, padding);
        fixed (byte* s = src) fixed (byte* y = dst.Y) fixed (byte* uv = dst.Uv)
            Reference(s, srcStride, width, height, y, dst.YStride, uv, dst.UvStride);
        return dst;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Whole_picture_matches_the_scalar_reference(int pathNumber)
    {
        var path = (PixelConvert.SimdPath)pathNumber;
        // Widths around the 8- and 16-pixel steps of the vector loops, so every tail length is covered.
        foreach (int width in new[] { 2, 4, 6, 8, 10, 14, 16, 18, 22, 24, 30, 32, 34, 46, 48, 50, 62, 64, 66, 158 })
            foreach (int height in new[] { 2, 4, 6, 18 })
            {
                int stride = width * 4 + 12;
                var src = Image(width, height, stride, width * 31 + height);
                var expected = RunReference(src, stride, width, height, padding: 6);
                var actual = new Nv12(width, height, padding: 6);
                Run(src, stride, width, height, actual, null, 1, path);
                Assert.True(expected.Y.AsSpan().SequenceEqual(actual.Y), $"Y differs at {width}x{height}");
                Assert.True(expected.Uv.AsSpan().SequenceEqual(actual.Uv), $"UV differs at {width}x{height}");
            }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_colour_pair_matches_the_scalar_reference(int pathNumber)
    {
        var path = (PixelConvert.SimdPath)pathNumber;
        // Each 2x2 block holds two colours from a sweep of the RGB cube, 17 levels per channel.
        var levels = Enumerable.Range(0, 17).Select(i => Math.Min(255, i * 16)).ToArray();
        var colours = (from r in levels from g in levels from b in levels select (r, g, b)).ToArray();
        int width = 64, blocks = colours.Length, height = (blocks / (width / 2) + 1) * 2, stride = width * 4;
        var src = new byte[stride * height];
        for (int k = 0; k < blocks; k++)
        {
            int bx = k % (width / 2) * 2, by = k / (width / 2) * 2;
            var c1 = colours[k];
            var c2 = colours[(k * 977) % blocks];
            foreach (var (dx, dy, c) in new[] { (0, 0, c1), (1, 0, c2), (0, 1, c2), (1, 1, c1) })
            {
                int i = (by + dy) * stride + (bx + dx) * 4;
                src[i] = (byte)c.b; src[i + 1] = (byte)c.g; src[i + 2] = (byte)c.r; src[i + 3] = 255;
            }
        }
        var expected = RunReference(src, stride, width, height, padding: 0);
        var actual = new Nv12(width, height, padding: 0);
        Run(src, stride, width, height, actual, null, 1, path);
        Assert.True(expected.Y.AsSpan().SequenceEqual(actual.Y), "Y differs");
        Assert.True(expected.Uv.AsSpan().SequenceEqual(actual.Uv), "UV differs");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Regions_update_only_their_blocks(int pathNumber)
    {
        var path = (PixelConvert.SimdPath)pathNumber;
        const int width = 200, height = 120;
        int stride = width * 4;
        var before = Image(width, height, stride, 1);
        var after = Image(width, height, stride, 2);
        // Odd edges, overlaps, one rectangle partly outside the picture and one empty.
        var regions = new List<DirtyRect>
        {
            new(3, 5, 17, 9), new(10, 8, 40, 3), new(101, 0, 1, 1), new(150, 100, 80, 40), new(0, 60, 200, 2), new(60, 60, 0, 10),
        };
        var actual = new Nv12(width, height, padding: 0);
        Run(before, stride, width, height, actual, null, 1, path);
        Run(after, stride, width, height, actual, regions, 1, path);

        var oldPicture = RunReference(before, stride, width, height, padding: 0);
        var newPicture = RunReference(after, stride, width, height, padding: 0);
        bool Covered(int bx, int by) => regions.Any(r => r.Width > 0 && r.Height > 0 && bx * 2 + 1 >= r.X && bx * 2 < r.X + r.Width && by * 2 + 1 >= r.Y && by * 2 < r.Y + r.Height);
        for (int by = 0; by < height / 2; by++)
            for (int bx = 0; bx < width / 2; bx++)
            {
                var source = Covered(bx, by) ? newPicture : oldPicture;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = (by * 2 + dy) * width + bx * 2 + dx;
                        Assert.True(source.Y[i] == actual.Y[i], $"Y at {bx * 2 + dx},{by * 2 + dy} (block {(Covered(bx, by) ? "changed" : "unchanged")})");
                    }
                int j = by * width + bx * 2;
                Assert.True(source.Uv[j] == actual.Uv[j] && source.Uv[j + 1] == actual.Uv[j + 1], $"UV of block {bx},{by}");
            }
    }

    [Fact]
    public void Parallel_bands_give_the_same_picture()
    {
        const int width = 1280, height = 720;
        int stride = width * 4;
        var src = Image(width, height, stride, 3);
        var single = new Nv12(width, height, 0);
        var many = new Nv12(width, height, 0);
        var regions = new[] { new DirtyRect(0, 0, width, height / 2), new DirtyRect(100, 300, 900, 420) };
        Run(src, stride, width, height, single, regions, 1, PixelConvert.BestPath);
        Run(src, stride, width, height, many, regions, 7, PixelConvert.BestPath);
        Assert.True(single.Y.AsSpan().SequenceEqual(many.Y));
        Assert.True(single.Uv.AsSpan().SequenceEqual(many.Uv));
        var expected = RunReference(src, stride, width, height, 0);
        var full = new Nv12(width, height, 0);
        Run(src, stride, width, height, full, null, PixelConvert.DefaultThreads, PixelConvert.BestPath);
        Assert.True(expected.Y.AsSpan().SequenceEqual(full.Y));
        Assert.True(expected.Uv.AsSpan().SequenceEqual(full.Uv));
    }
}

public class DirtyRegionTests
{
    private static bool[,] Mask(DirtyRegion region)
    {
        var mask = new bool[region.Width, region.Height];
        foreach (var r in region.GetRects())
            for (int y = r.Y; y < r.Bottom; y++)
                for (int x = r.X; x < r.Right; x++)
                {
                    Assert.False(mask[x, y], $"rectangles overlap at {x},{y}");
                    mask[x, y] = true;
                }
        return mask;
    }

    [Fact]
    public void Starts_empty_and_clears()
    {
        var region = new DirtyRegion(100, 50);
        Assert.True(region.IsEmpty);
        Assert.Empty(region.GetRects());
        region.Add(10, 10, 5, 5);
        Assert.False(region.IsEmpty);
        region.Clear();
        Assert.True(region.IsEmpty);
        Assert.Empty(region.GetRects());
        Assert.Equal(0, region.Coverage);
    }

    [Fact]
    public void Covers_every_added_pixel_on_whole_tiles_without_overlap()
    {
        var region = new DirtyRegion(150, 70); // partial tiles on the right and at the bottom
        var added = new[] { new DirtyRect(5, 5, 20, 20), new DirtyRect(18, 18, 3, 3), new DirtyRect(140, 60, 50, 50), new DirtyRect(-10, 40, 15, 2), new DirtyRect(64, 0, 1, 70) };
        foreach (var r in added) region.Add(r);
        var mask = Mask(region);
        foreach (var r in added)
            for (int y = Math.Max(0, r.Y); y < Math.Min(70, r.Bottom); y++)
                for (int x = Math.Max(0, r.X); x < Math.Min(150, r.Right); x++)
                    Assert.True(mask[x, y], $"{x},{y} not covered");
        foreach (var r in region.GetRects())
        {
            Assert.Equal(0, r.X % DirtyRegion.TileSize);
            Assert.Equal(0, r.Y % DirtyRegion.TileSize);
            Assert.True(r.Right <= 150 && r.Bottom <= 70);
        }
        // A pixel far from every rectangle stays clean.
        Assert.False(mask[100, 30]);
    }

    [Fact]
    public void Merges_tile_runs_into_few_rectangles()
    {
        var region = new DirtyRegion(1920, 1080);
        region.Add(100, 100, 400, 300); // tiles 6..31 x 6..25: one block
        Assert.Single(region.GetRects());
        Assert.Equal(new DirtyRect(96, 96, 416, 304), region.GetRects()[0]);
        region.AddAll();
        Assert.True(region.IsFull);
        Assert.Equal(new[] { new DirtyRect(0, 0, 1920, 1080) }, region.GetRects());
        Assert.Equal(1.0, region.Coverage, 6);
    }

    [Fact]
    public void Adds_a_region_of_another_size()
    {
        var small = new DirtyRegion(101, 51);
        small.Add(90, 40, 11, 11);
        var large = new DirtyRegion(100, 50);
        large.Add(small);
        var mask = Mask(large);
        Assert.True(mask[99, 49]);
        Assert.True(mask[96, 32]);
        Assert.False(mask[0, 0]);
        var same = new DirtyRegion(101, 51);
        same.Add(small);
        Assert.Equal(small.GetRects(), same.GetRects());
    }

    [Fact]
    public void Coverage_is_the_share_of_pixels()
    {
        var region = new DirtyRegion(64, 64);
        region.Add(0, 0, 1, 1);
        Assert.Equal(1 / 16.0, region.Coverage, 6);
        region.Add(32, 32, 32, 32);
        Assert.Equal(5 / 16.0, region.Coverage, 6);
    }
}
