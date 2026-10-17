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
