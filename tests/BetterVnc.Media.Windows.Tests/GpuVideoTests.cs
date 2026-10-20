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
