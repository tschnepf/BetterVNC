using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Windows.Tests;

public class H264EncoderRegionTests
{
    private static byte[] Frame(int w, int h, int t)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool box = x >= 20 + t * 6 && x < 80 + t * 6 && y >= 30 && y < 90;
                bgra[i] = (byte)(box ? 200 : x * 255 / w);
                bgra[i + 1] = (byte)(box ? 60 : y * 255 / h);
                bgra[i + 2] = (byte)(box ? 40 : 120);
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    private static double MeanError(byte[] a, byte[] b)
    {
        double total = 0;
        for (int i = 0; i < a.Length; i += 4)
            total += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return total / (a.Length / 4 * 3);
    }

    [Fact]
    public unsafe void Only_changed_regions_need_converting()
    {
        const int w = 320, h = 240;
        using var enc = new H264Encoder(w, h, 30, 2000, preferHardware: false);
        var frames = new List<EncodedFrame>();
        var region = new DirtyRegion(w, h);
        for (int t = 0; t < 10; t++)
        {
            var bgra = Frame(w, h, t);
            region.Clear();
            if (t > 0) region.Add(20 + (t - 1) * 6, 30, 66, 60); // where the box was and is now
            fixed (byte* p = bgra) enc.Encode(p, w * 4, t == 0 ? null : region, t * 33_333L);
            enc.Pump(frames);
            Assert.Equal(0, enc.InFlight); // the software encoder hands each frame back at once: one input buffer is enough
        }
        enc.Drain(frames);
        Assert.Equal(10, frames.Count);
        Assert.All(frames, f => Assert.True(f.EncodeMicros >= 0));

        using var dec = new H264Decoder();
        var bgrx = new byte[w * h * 4];
        double worst = 0;
        int decoded = 0;
        foreach (var f in frames)
            dec.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) =>
            {
                d.ToBgrx(bgrx);
                worst = Math.Max(worst, MeanError(Frame(w, h, (int)(d.TimestampMicros / 33_333L)), bgrx));
                decoded++;
            });
        Assert.True(decoded >= 8, $"decoded {decoded}");
        Assert.True(worst < 12, $"worst mean channel error {worst:0.0}");
    }

    [Fact]
    public unsafe void A_region_that_misses_a_change_leaves_it_out()
    {
        // Proves the region is honoured: a change outside it does not reach the encoder.
        const int w = 320, h = 240;
        using var enc = new H264Encoder(w, h, 30, 4000, preferHardware: false);
        var frames = new List<EncodedFrame>();
        var first = Frame(w, h, 0);
        fixed (byte* p = first) enc.Encode(p, w * 4, null, 0);
        enc.Pump(frames); // out before the next goes in, or the encoder would rightly use a fresh, fully converted buffer
        var moved = Frame(w, h, 20); // box far to the right
        var nothing = new DirtyRegion(w, h);
        fixed (byte* p = moved) enc.Encode(p, w * 4, nothing, 33_333);
        enc.Pump(frames);
        enc.Drain(frames);
        using var dec = new H264Decoder();
        var bgrx = new byte[w * h * 4];
        byte[]? last = null;
        foreach (var f in frames)
            dec.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) => { d.ToBgrx(bgrx); last = (byte[])bgrx.Clone(); });
        Assert.NotNull(last);
        Assert.True(MeanError(first, last!) < MeanError(moved, last!), "the unreported change was encoded");
    }
}
