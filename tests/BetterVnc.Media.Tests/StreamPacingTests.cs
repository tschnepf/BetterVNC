using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

public class FrameRateGovernorTests
{
    /// <summary>Feeds <paramref name="seconds"/> of frames at the governor's current rate, each taking <paramref name="workMs"/>.</summary>
    private static List<string> Run(FrameRateGovernor g, ref long now, double seconds, double workMs)
    {
        var changes = new List<string>();
        long end = now + (long)(seconds * 1e6);
        while (now < end)
        {
            now += 1_000_000 / g.Fps;
            if (g.Record((long)(workMs * 1000), now) is { } c) changes.Add(c);
        }
        return changes;
    }

    [Fact]
    public void Steps_down_when_frames_take_most_of_the_interval_and_back_up_with_headroom()
    {
        var g = new FrameRateGovernor(60);
        long now = 0;
        Assert.Empty(Run(g, ref now, 3, workMs: 8)); // 48% of 16.7 ms: fine
        Assert.Equal(60, g.Fps);

        var down = Run(g, ref now, 2.2, workMs: 14); // 84%
        Assert.Single(down);
        Assert.Equal(30, g.Fps);
        Assert.Contains("stepping down to 30 fps", down[0]);
        Run(g, ref now, 2.5, workMs: 28); // 84% of 33 ms
        Assert.Equal(20, g.Fps);
        Run(g, ref now, 3, workMs: 45); // nothing below 20
        Assert.Equal(20, g.Fps);
        Assert.True(g.SteppedDown);

        // 14 ms is 42% of 33 ms but 84% of 16.7 ms: back to 30 after a few seconds, not to 60.
        var up = Run(g, ref now, 5.5, workMs: 14);
        Assert.Single(up);
        Assert.Contains("stepping back up to 30 fps", up[0]);
        Assert.Equal(30, g.Fps);
        Run(g, ref now, 6, workMs: 14);
        Assert.Equal(30, g.Fps);
        Run(g, ref now, 5.5, workMs: 3);
        Assert.Equal(60, g.Fps);
        Assert.False(g.SteppedDown);
    }

    [Fact]
    public void A_short_burst_or_a_still_screen_changes_nothing()
    {
        var g = new FrameRateGovernor(60);
        long now = 0;
        for (int i = 0; i < 3; i++) Assert.Null(g.Record(30_000, now += 16_667)); // a few slow frames, then nothing
        now += 5_000_000;
        Assert.Null(g.Record(30_000, now)); // the window had too few frames to judge
        Assert.Equal(60, g.Fps);
    }

    [Fact]
    public void The_ladder_starts_at_the_rate_asked_for()
    {
        var g = new FrameRateGovernor(24);
        long now = 0;
        Run(g, ref now, 1.5, workMs: 40);
        Assert.Equal(20, g.Fps);
        g.Reset(120);
        Assert.Equal(120, g.Fps);
        Run(g, ref now, 1.5, workMs: 7); // 84% of 8.3 ms
        Assert.Equal(60, g.Fps);
        var slow = new FrameRateGovernor(15);
        Run(slow, ref now, 3, workMs: 60);
        Assert.Equal(15, slow.Fps);
    }
}

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
