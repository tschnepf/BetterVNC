using System.Runtime.InteropServices;
using BetterVnc.Media;
using BetterVnc.Media.Interop;
using Xunit;

namespace BetterVnc.Media.Windows.Tests;

/// <summary>Hardware decode and GPU presentation. Skipped where the machine has no Direct3D 11 GPU.</summary>
public class GpuVideoTests
{
    private static D3D11VideoDevice DeviceOrSkip()
    {
        try { return D3D11VideoDevice.Create(); }
        catch (MediaException ex)
        {
            Assert.Skip("No Direct3D 11 GPU here: " + ex.Message);
            throw;
        }
    }

    /// <summary>A moving box on a gradient; 180 rows is not a whole number of macroblocks, so decoders pad and crop.</summary>
    private static byte[] Frame(int w, int h, int t)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool box = x >= 20 + t * 6 && x < 90 + t * 6 && y >= 40 && y < 110;
                bgra[i] = (byte)(box ? 210 : x * 255 / w);
                bgra[i + 1] = (byte)(box ? 50 : y * 255 / h);
                bgra[i + 2] = (byte)(box ? 30 : 110);
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    private static List<EncodedFrame> Encode(int w, int h, int count)
    {
        using var enc = new H264Encoder(w, h, 30, 3000, preferHardware: false);
        var frames = new List<EncodedFrame>();
        for (int t = 0; t < count; t++)
        {
            enc.Encode(Frame(w, h, t), w * 4, t * 33_333L);
            enc.Pump(frames);
        }
        enc.Drain(frames);
        return frames;
    }

    private static double MeanError(byte[] a, byte[] b)
    {
        double total = 0;
        for (int i = 0; i < a.Length; i += 4)
            total += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return total / (a.Length / 4 * 3);
    }

    [Fact]
    public void Device_has_video_support_and_a_name()
    {
        using var device = DeviceOrSkip();
        Assert.False(string.IsNullOrWhiteSpace(device.AdapterName));
        Assert.True(device.SupportsVideo, $"{device.AdapterName}: created without D3D11_CREATE_DEVICE_VIDEO_SUPPORT");
        Assert.True(device.SupportsNv12Textures, $"{device.AdapterName}: NV12 cannot be sampled");
    }

    [Fact]
    public void Hardware_decode_delivers_textures_that_render_like_the_source()
    {
        const int w = 320, h = 180;
        var frames = Encode(w, h, 10);
        using var device = DeviceOrSkip();
        using var decoder = new H264Decoder(device);
        if (!decoder.IsHardware) Assert.Skip($"{device.AdapterName}: hardware decode refused ({decoder.HardwareFailure})");
        using var renderer = new VideoRenderer(device);
        int decoded = 0, textures = 0;
        double worst = 0;
        foreach (var f in frames)
        {
            decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) =>
            {
                decoded++;
                if (d.IsTexture) textures++;
                Assert.Equal(w, d.Width);
                Assert.Equal(h, d.Height);
                Assert.True(renderer.Present(in d), renderer.Failure ?? "Present refused the frame");
                var pixels = renderer.CaptureFrame(out int cw, out int ch);
                Assert.NotNull(pixels);
                Assert.Equal((w, h), (cw, ch));
                int t = (int)Math.Round(d.TimestampMicros / 33_333.0);
                worst = Math.Max(worst, MeanError(Frame(w, h, t), pixels!));
            });
        }
        Assert.True(decoded >= frames.Count - 2, $"decoded {decoded} of {frames.Count}");
        Assert.Equal(decoded, textures);
        Assert.True(worst < 12, $"worst mean channel error {worst:0.0} through {decoder.Name} on {device.AdapterName}");
    }

    [Fact]
    public void Hardware_decode_follows_a_resolution_change()
    {
        var small = Encode(160, 120, 4);
        var large = Encode(320, 180, 4);
        using var device = DeviceOrSkip();
        using var decoder = new H264Decoder(device);
        if (!decoder.IsHardware) Assert.Skip($"{device.AdapterName}: hardware decode refused ({decoder.HardwareFailure})");
        using var renderer = new VideoRenderer(device);
        var sizes = new List<(int, int)>();
        foreach (var f in small.Concat(large))
            decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) =>
            {
                Assert.True(d.IsTexture);
                Assert.True(renderer.Present(in d), renderer.Failure);
                sizes.Add((d.Width, d.Height));
            });
        Assert.Contains((160, 120), sizes);
        Assert.Equal((320, 180), sizes[^1]);
        Assert.Equal((320, 180), (renderer.FrameWidth, renderer.FrameHeight));
    }

    [Fact]
    public void A_device_that_cannot_decode_leaves_the_decoder_in_software()
    {
        var frames = Encode(160, 120, 3);
        var device = DeviceOrSkip();
        device.Dispose(); // no manager to hand out any more: the same path as a driver without video support
        using var decoder = new H264Decoder(device);
        Assert.False(decoder.IsHardware);
        Assert.NotNull(decoder.HardwareFailure);
        int memoryFrames = 0;
        foreach (var f in frames)
            decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) => { if (!d.IsTexture && d.Y != IntPtr.Zero) memoryFrames++; });
        Assert.True(memoryFrames >= 2, $"{memoryFrames} software frames");
    }

    [Fact]
    public void Software_frames_render_like_the_cpu_conversion()
    {
        const int w = 320, h = 180;
        var frames = Encode(w, h, 4);
        using var device = DeviceOrSkip();
        using var decoder = new H264Decoder();
        Assert.False(decoder.IsHardware);
        using var renderer = new VideoRenderer(device);
        var cpu = new byte[w * h * 4];
        double worst = 0;
        int seen = 0;
        foreach (var f in frames)
        {
            decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) =>
            {
                Assert.False(d.IsTexture);
                d.ToBgrx(cpu);
                Assert.True(renderer.Present(in d), renderer.Failure ?? "Present refused the frame");
                var gpu = renderer.CaptureFrame(out _, out _)!;
                worst = Math.Max(worst, MeanError(cpu, gpu));
                seen++;
            });
        }
        Assert.True(seen > 0);
        // The shader interpolates chroma where the CPU repeats it; on soft content the two agree closely.
        Assert.True(worst < 2.5, $"GPU and CPU conversions differ by {worst:0.00} per channel");
    }

    [Fact]
    public void Bgrx_frames_render_unchanged()
    {
        const int w = 64, h = 37;
        var bgrx = Frame(w, h, 1);
        using var device = DeviceOrSkip();
        using var renderer = new VideoRenderer(device);
        Assert.True(renderer.Present(bgrx, w, h));
        var back = renderer.CaptureFrame(out int cw, out int ch)!;
        Assert.Equal((w, h), (cw, ch));
        for (int i = 0; i < bgrx.Length; i += 4)
            for (int c = 0; c < 3; c++)
                Assert.InRange(back[i + c], Math.Max(0, bgrx[i + c] - 1), Math.Min(255, bgrx[i + c] + 1));
    }

    /// <summary>Uploads <paramref name="rects"/> of <paramref name="source"/> (w*4 bytes per row) into the renderer's framebuffer.</summary>
    private static unsafe bool Upload(VideoRenderer renderer, byte[] source, int w, int h, params (int X, int Y, int W, int H)[] rects) =>
        renderer.UpdateFramebuffer(target =>
        {
            bool fresh = target.EnsureSize(w, h);
            fixed (byte* p = source)
                foreach (var r in fresh ? new[] { (0, 0, w, h) } : rects)
                    target.Copy((IntPtr)(p + (r.Item2 * w + r.Item1) * 4), w * 4, r.Item1, r.Item2, r.Item3, r.Item4);
        });

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var bgrx = new byte[w * h * 4];
        for (int i = 0; i < bgrx.Length; i += 4) { bgrx[i] = b; bgrx[i + 1] = g; bgrx[i + 2] = r; bgrx[i + 3] = 255; }
        return bgrx;
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i += 4)
            for (int c = 0; c < 3; c++)
                if (Math.Abs(expected[i + c] - actual[i + c]) > 1)
                    Assert.Fail($"{what}: pixel {i / 4} channel {c} is {actual[i + c]}, expected {expected[i + c]}");
    }

    [Fact]
    public void Only_the_changed_rectangles_of_the_framebuffer_are_uploaded()
    {
        const int w = 64, h = 40;
        var source = Frame(w, h, 0);
        using var device = DeviceOrSkip();
        using var renderer = new VideoRenderer(device);
        Upload(renderer, source, w, h);
        Assert.Equal((w, h), (renderer.FramebufferWidth, renderer.FramebufferHeight));
        AssertSame(source, renderer.CaptureFrame(out int cw, out int ch)!, "first upload");
        Assert.Equal((w, h), (cw, ch));

        // Two parts change, only one is uploaded: the picture shows exactly that one.
        var changed = (byte[])source.Clone();
        var expected = (byte[])source.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool first = x >= 5 && x < 21 && y >= 3 && y < 13, second = x >= 40 && x < 60 && y >= 20 && y < 38;
                if (first || second) { changed[i] = 255; changed[i + 1] = 0; changed[i + 2] = 255; }
                if (first) { expected[i] = 255; expected[i + 1] = 0; expected[i + 2] = 255; }
            }
        Upload(renderer, changed, w, h, (5, 3, 16, 10));
        AssertSame(expected, renderer.CaptureFrame(out _, out _)!, "dirty rectangle");
    }

    [Fact]
    public void A_partial_stream_lands_on_its_part_of_the_framebuffer()
    {
        const int w = 64, h = 40;
        var grey = Solid(w, h, 90, 90, 90);
        using var device = DeviceOrSkip();
        using var renderer = new VideoRenderer(device);
        Upload(renderer, grey, w, h);
        // The primary display of a larger desktop: 16x10 at 8,6, and one that reaches past the framebuffer's corner.
        Assert.True(renderer.Present(Solid(16, 10, 0, 0, 230), 16, 10, (8, 6)));
        var shot = renderer.CaptureFrame(out int cw, out int ch)!;
        Assert.Equal((w, h), (cw, ch));
        var expected = (byte[])grey.Clone();
        for (int y = 6; y < 16; y++)
            for (int x = 8; x < 24; x++) { int i = (y * w + x) * 4; expected[i] = 0; expected[i + 1] = 0; expected[i + 2] = 230; }
        AssertSame(expected, shot, "partial stream");

        Assert.True(renderer.Present(Solid(16, 10, 0, 200, 0), 16, 10, (56, 34)));
        expected = (byte[])grey.Clone();
        for (int y = 34; y < h; y++)
            for (int x = 56; x < w; x++) { int i = (y * w + x) * 4; expected[i] = 0; expected[i + 1] = 200; expected[i + 2] = 0; }
        AssertSame(expected, renderer.CaptureFrame(out _, out _)!, "clipped partial stream");

        // Framebuffer changes around the stream still show while it runs.
        var lighter = Solid(w, h, 150, 150, 150);
        Upload(renderer, lighter, w, h, (0, 0, 8, 8));
        Assert.Equal(150, renderer.CaptureFrame(out _, out _)![0]);
    }

    [Fact]
    public void A_whole_desktop_stream_covers_the_framebuffer_until_hidden()
    {
        const int w = 64, h = 40;
        var source = Frame(w, h, 1);
        using var device = DeviceOrSkip();
        using var renderer = new VideoRenderer(device);
        Upload(renderer, source, w, h);
        // A scaled stream (half size) of the whole desktop: the screenshot is the stream at its own size.
        var red = Solid(32, 20, 0, 0, 230);
        Assert.True(renderer.Present(red, 32, 20));
        Assert.True(renderer.IsStreamShown);
        var shot = renderer.CaptureFrame(out int cw, out int ch)!;
        Assert.Equal((32, 20), (cw, ch));
        AssertSame(red, shot, "stream");
        // The framebuffer keeps taking updates underneath, without presenting.
        source[0] = 7;
        Assert.False(Upload(renderer, source, w, h, (0, 0, 1, 1)));
        renderer.HideStream();
        Assert.False(renderer.IsStreamShown);
        var back = renderer.CaptureFrame(out cw, out ch)!;
        Assert.Equal((w, h), (cw, ch));
        AssertSame(source, back, "back to the framebuffer");
    }

    [Fact]
    public void Presents_into_a_window_and_survives_resizes()
    {
        const int w = 320, h = 180;
        var frames = Encode(w, h, 6);
        using var device = DeviceOrSkip();
        var hwnd = CreateWindowExW(0, "Static", "BetterVNC test", WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            using var decoder = new H264Decoder(device);
            using var renderer = new VideoRenderer(device);
            renderer.SetWindow(hwnd);
            renderer.SetLayout(w, h, 0, 0, w, h);
            int n = 0;
            foreach (var f in frames)
            {
                decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame d) => Assert.True(renderer.Present(in d), renderer.Failure));
                // Fit into a smaller window, then a wider one with bars, then hide: what a session window does.
                if (++n == 2) renderer.SetLayout(160, 90, 0, 0, 160, 90);
                if (n == 3) renderer.SetLayout(400, 180, 40, 0, 320, 180);
                if (n == 4) renderer.SetLayout(0, 0, 0, 0, 0, 0);
            }
            Assert.True(renderer.IsUsable, renderer.Failure);
            Assert.True(renderer.FramesPresented >= 4, $"{renderer.FramesPresented} presents");
            renderer.SetWindow(IntPtr.Zero);
        }
        finally { DestroyWindow(hwnd); }
    }

    private const uint WS_POPUP = 0x80000000;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
