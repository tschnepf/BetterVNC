using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

public class PixelConvertTests
{
    [Fact]
    public void Bgra_to_nv12_and_back_is_close_to_the_original()
    {
        const int w = 64, h = 48;
        var bgra = new byte[w * h * 4];
        var rnd = new Random(1);
        // Smooth gradient plus noise: chroma subsampling blurs sharp colour edges, so compare on soft content.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = (byte)Math.Clamp(x * 4 + rnd.Next(-3, 4), 0, 255);
                bgra[i + 1] = (byte)Math.Clamp(y * 5 + rnd.Next(-3, 4), 0, 255);
                bgra[i + 2] = (byte)Math.Clamp(128 + rnd.Next(-3, 4), 0, 255);
                bgra[i + 3] = 255;
            }
        var nv12 = new byte[w * h * 3 / 2];
        PixelConvert.BgraToNv12(bgra, w * 4, w, h, nv12);
        var back = new byte[w * h * 4];
        PixelConvert.Nv12ToBgrx(nv12, w, h, back);
        double total = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            total += Math.Abs(bgra[i] - back[i]) + Math.Abs(bgra[i + 1] - back[i + 1]) + Math.Abs(bgra[i + 2] - back[i + 2]);
            Assert.Equal(255, back[i + 3]);
        }
        double meanError = total / (w * h * 3);
        Assert.True(meanError < 4, $"mean channel error {meanError:0.00}");
    }

    [Fact]
    public void Grey_levels_round_trip_exactly_enough()
    {
        const int w = 16, h = 2;
        var bgra = new byte[w * h * 4];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)(x * 17);
            }
        var nv12 = new byte[w * h * 3 / 2];
        PixelConvert.BgraToNv12(bgra, w * 4, w, h, nv12);
        var back = new byte[w * h * 4];
        PixelConvert.Nv12ToBgrx(nv12, w, h, back);
        for (int i = 0; i < bgra.Length; i += 4)
            Assert.InRange(back[i], Math.Max(0, bgra[i] - 3), Math.Min(255, bgra[i] + 3));
    }
}

public class AnnexBTests
{
    [Fact]
    public void Finds_nal_units_and_parameter_sets()
    {
        var sps = new byte[] { 0x67, 0x42, 0x00, 0x1e };
        var pps = new byte[] { 0x68, 0xce, 0x38, 0x80 };
        var idr = new byte[] { 0x65, 0x88, 0x84, 0x00 };
        var stream = new byte[] { 0, 0, 0, 1 }.Concat(sps).Concat(new byte[] { 0, 0, 0, 1 }).Concat(pps).Concat(new byte[] { 0, 0, 1 }).Concat(idr).ToArray();
        var nals = AnnexB.Parse(stream);
        Assert.Equal(new[] { 7, 8, 5 }, nals.Select(n => n.Type));
        AnnexB.Inspect(stream, out bool hasIdr, out bool hasSps, out bool hasPps);
        Assert.True(hasIdr && hasSps && hasPps);
        var header = AnnexB.ExtractParameterSets(stream)!;
        Assert.Equal(new byte[] { 0, 0, 0, 1 }.Concat(sps).Concat(new byte[] { 0, 0, 0, 1 }).Concat(pps).ToArray(), header);
        Assert.Null(AnnexB.ExtractParameterSets(new byte[] { 0, 0, 1 }.Concat(idr).ToArray()));
    }
}
