using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Windows.Tests;

public class H264RoundTripTests
{
    private static byte[] Frame(int w, int h, int t)
    {
        // A moving soft-edged box on a gradient: easy to encode, easy to compare.
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool box = x >= 20 + t * 4 && x < 80 + t * 4 && y >= 30 && y < 90;
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

    private static List<EncodedFrame> EncodeAll(H264Encoder enc, int w, int h, int count, Func<int, bool>? keyframeAt = null)
    {
        var frames = new List<EncodedFrame>();
        for (int t = 0; t < count; t++)
        {
            var deadline = Environment.TickCount64 + 2000;
            while (!enc.WantsInput)
            {
                enc.Pump(frames);
                Assert.True(Environment.TickCount64 < deadline, "encoder never asked for input");
                Thread.Sleep(1);
            }
            if (keyframeAt?.Invoke(t) == true) Assert.True(enc.ForceKeyframe(), "ForceKeyframe refused");
            enc.Encode(Frame(w, h, t), w * 4, t * 16_667L);
            enc.Pump(frames);
        }
        enc.Drain(frames);
        return frames;
    }

    [Fact]
    public void An_encoder_is_available_on_this_machine()
    {
        var name = H264Encoder.DescribeAvailable();
        Assert.False(string.IsNullOrEmpty(name), "no H.264 encoder found");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Frames_survive_encode_and_decode(bool preferHardware)
    {
        const int w = 320, h = 240;
        using var enc = new H264Encoder(w, h, 30, 2000, preferHardware);
        var frames = EncodeAll(enc, w, h, 12, t => t == 6);
        Assert.True(frames.Count >= 10, $"only {frames.Count} frames came out of {enc.Name}");
        Assert.True(frames[0].Keyframe, "first frame is not a key frame");
        AnnexB.Inspect(frames[0].Data, out bool idr, out bool sps, out bool pps);
        Assert.True(idr && sps && pps, "first key frame lacks IDR/SPS/PPS");
        var later = frames.Skip(1).Where(f => f.Keyframe).ToList();
        Assert.True(later.Count >= 1, "the forced key frame did not appear");
        foreach (var k in later)
        {
            AnnexB.Inspect(k.Data, out _, out bool s, out bool p);
            Assert.True(s && p, "a later key frame lacks SPS/PPS");
        }
        Assert.Contains(frames.Skip(1), f => !f.Keyframe);
        Assert.True(enc.SetBitrate(1000), "SetBitrate refused");

        using var dec = new H264Decoder();
        int decoded = 0;
        double worst = 0;
        var bgrx = new byte[w * h * 4];
        for (int i = 0; i < frames.Count; i++)
        {
            int n = i;
            dec.Decode(frames[i].Data, frames[i].TimestampMicros, (in DecodedFrame f) =>
            {
                Assert.Equal(w, f.Width);
                Assert.Equal(h, f.Height);
                f.ToBgrx(bgrx);
                // Pictures come out in order and without delay; match on the timestamp to be sure.
                int t = (int)(f.TimestampMicros / 16_667L);
                worst = Math.Max(worst, MeanError(Frame(w, h, t), bgrx));
                decoded++;
            });
        }
        Assert.True(decoded >= frames.Count - 2, $"decoded {decoded} of {frames.Count}");
        Assert.True(worst < 12, $"worst mean channel error {worst:0.0} ({enc.Name} -> {dec.Name})");
    }

    [Fact]
    public void Decoder_follows_a_resolution_change()
    {
        using var small = new H264Encoder(160, 120, 30, 1000, preferHardware: false);
        var a = EncodeAll(small, 160, 120, 4);
        using var large = new H264Encoder(320, 240, 30, 1000, preferHardware: false);
        var b = EncodeAll(large, 320, 240, 4);
        using var dec = new H264Decoder();
        var sizes = new List<(int, int)>();
        foreach (var f in a.Concat(b))
            dec.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) => sizes.Add((d.Width, d.Height)));
        Assert.Contains((160, 120), sizes);
        Assert.Contains((320, 240), sizes);
        Assert.Equal((320, 240), sizes[^1]);
        Assert.Equal(320, dec.Width);
    }
}

public class DesktopDuplicatorTests
{
    [Fact]
    public void Captures_the_desktop_when_duplication_is_available()
    {
        DesktopDuplicator dup;
        try { dup = DesktopDuplicator.Create(); }
        catch (Exception ex)
        {
            Assert.Skip("Desktop Duplication unavailable here: " + ex.Message);
            return;
        }
        using (dup)
        {
            Assert.True(dup.Width > 0 && dup.Height > 0);
            var bgra = new byte[dup.Width * dup.Height * 4];
            // The first acquire after creation delivers the current desktop.
            bool got = false;
            for (int i = 0; i < 20 && !got; i++) got = dup.TryCapture(100, bgra);
            Assert.True(got, "no desktop frame within two seconds");
        }
    }
}
