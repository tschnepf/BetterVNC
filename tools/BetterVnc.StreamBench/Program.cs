using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterVnc.App.Controls;
using BetterVnc.Media;
using BetterVnc.Protocol;

namespace BetterVnc.StreamBench;

// Usage: BetterVnc.StreamBench --mode before|gpu|sw-gpu [--seconds 20] [--fps 60] [--width 1920] [--height 1080] [--kbps 20000]
//   before  software decode, CPU NV12->BGRX, WritePixels + WPF (the viewer's path before GPU presentation; still its fallback)
//   gpu     DXVA decode on the view's device, texture copied and presented by the swap chain
//   sw-gpu  software decode, NV12 planes uploaded and converted by the swap chain's shader
// Prints one result line; exit code 0 when the on-screen checks passed.
internal static class Program
{
    private static string Mode = "gpu";
    private static int Seconds = 20, Fps = 60, Width = 1920, Height = 1080, Kbps = 20000;
    private const int WarmupSeconds = 3;
    private static readonly StringBuilder Report = new();

    // The framebuffer colour (RFB picture) and the stream's static centre square, told apart on screen.
    private const byte FbB = 128, FbG = 0, FbR = 0;

    [STAThread]
    private static int Main(string[] args)
    {
        string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        Mode = Opt("--mode") ?? Mode;
        Seconds = int.TryParse(Opt("--seconds"), out var s) ? s : Seconds;
        Fps = int.TryParse(Opt("--fps"), out var f) ? f : Fps;
        Width = int.TryParse(Opt("--width"), out var w) ? w : Width;
        Height = int.TryParse(Opt("--height"), out var h) ? h : Height;
        Kbps = int.TryParse(Opt("--kbps"), out var k) ? k : Kbps;
        if (Mode is not ("before" or "gpu" or "sw-gpu")) { Console.Error.WriteLine("--mode before|gpu|sw-gpu"); return 2; }
        timeBeginPeriod(1);

        var stream = EncodeStream();
        int port = StartFakeServer();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var view = new VncView { ScaleMode = BetterVnc.Viewer.Core.Models.ScaleMode.Fit };
        Scroller = new System.Windows.Controls.ScrollViewer
        {
            Content = view, Background = Brushes.Black, Focusable = false,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
        };
        var window = new Window
        {
            Title = "BetterVNC stream bench", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
            Left = 0, Top = 0, Width = 640, Height = 360, Background = Brushes.Black, Content = Scroller, ShowActivated = true,
        };
        BenchWindowObject = window;
        int exit = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Width = Width / dpi.DpiScaleX;   // the picture lands 1:1 on device pixels
                window.Height = Height / dpi.DpiScaleY;
                var client = new VncClient(new VncConnectionOptions { Host = "127.0.0.1", Port = port, EnableQemuAudio = false });
                await client.ConnectAsync();
                view.Attach(client);
                await WaitUntil(() => client.Framebuffer.Width == Width && client.Statistics.FramebufferUpdates > 0, 5000);
                await Task.Delay(300);
                if (Mode != "before" && !view.EnableGpuVideo()) throw new InvalidOperationException(view.GpuVideoStatus);
                var hwnd = new WindowInteropHelper(window).Handle;
                exit = await Task.Run(() => Play(view, stream, hwnd));
                client.Dispose();
            }
            catch (Exception ex) { Line("FAILED: " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        Console.WriteLine(Report.ToString().TrimEnd());
        timeEndPeriod(1);
        return exit;
    }

    private static System.Windows.Controls.ScrollViewer Scroller = null!;
    private static Window BenchWindowObject = null!;

    private static void Line(string s) { Report.AppendLine(s); }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the fake VNC server's picture never arrived");
            await Task.Delay(20);
        }
    }

    // ------------------------------------------------------------------ playback (mirrors VideoStreamPlayer's H.264 path)

    private static int Play(VncView view, List<EncodedFrame> stream, IntPtr hwnd)
    {
        BenchWindow = hwnd;
        var pool = new ConcurrentBag<byte[]>();
        byte[] Rent(int size) { while (pool.TryTake(out var b)) if (b.Length == size) return b; return new byte[size]; }
        void Return(byte[] b) { if (pool.Count < 4) pool.Add(b); }

        using var decoder = new H264Decoder(Mode == "gpu" ? view.GpuVideoDevice : null);
        int fallbacks = 0;
        bool sawTexture = false;
        void DecodeOne(EncodedFrame frame)
        {
            decoder.Decode(frame.Data, frame.TimestampMicros, (in DecodedFrame picture) =>
            {
                if (picture.IsTexture) sawTexture = true;
                if (Mode != "before" && view.PresentVideoFrame(in picture)) return;
                if (Mode != "before") fallbacks++;
                var bgrx = Rent(picture.Width * picture.Height * 4);
                picture.ToBgrx(bgrx);
                view.PresentExternalFrame(bgrx, picture.Width, picture.Height, Return);
            });
        }

        var clock = Stopwatch.StartNew();
        long interval = Stopwatch.Frequency / Fps;
        long next = clock.ElapsedTicks;
        int index = 0;
        void PlayFor(double seconds, List<double>? costs)
        {
            long end = clock.ElapsedTicks + (long)(seconds * Stopwatch.Frequency);
            while (clock.ElapsedTicks < end)
            {
                while (clock.ElapsedTicks < next) Thread.Sleep(1);
                next += interval;
                long t0 = clock.ElapsedTicks;
                DecodeOne(stream[index++ % stream.Count]);
                costs?.Add((clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
            }
        }

        PlayFor(WarmupSeconds, null);
        var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var thread0 = ThreadCpu();
        long wall0 = clock.ElapsedTicks;
        int fed0 = index;
        var costs = new List<double>();
        double fps = 0;
        int fpsSamples = 0;
        var sampler = new Timer(_ => { fps += view.Dispatcher.Invoke(() => view.Fps); fpsSamples++; }, null, 1000, 1000);
        PlayFor(Seconds, costs);
        sampler.Dispose();
        double wall = (clock.ElapsedTicks - wall0) / (double)Stopwatch.Frequency;
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpu0).TotalSeconds;
        double decodeThread = (ThreadCpu() - thread0).TotalSeconds;
        int fed = index - fed0;
        costs.Sort();

        string path = Mode switch
        {
            "before" => "software decode, CPU NV12->BGRX, WritePixels + WPF",
            "gpu" => sawTexture ? "DXVA decode, GPU copy + shader, flip-model swap chain" : "DXVA REFUSED, software decode + swap chain",
            _ => "software decode, NV12 upload + shader, flip-model swap chain",
        };
        Line($"mode={Mode}  {Width}x{Height}@{Fps} {Kbps / 1000.0:0.#} Mbit/s  decoder: {decoder.Name} ({(decoder.IsHardware ? "hardware" : "software")}{(decoder.HardwareFailure != null ? ", " + decoder.HardwareFailure : "")})");
        Line($"  path: {path}; {(Mode == "before" ? "WPF" : view.GpuVideoStatus)}");
        Line($"  frames fed {fed} in {wall:0.0} s ({fed / wall:0.0}/s), view fps {(fpsSamples > 0 ? fps / fpsSamples : 0):0.0}, GPU fallbacks {fallbacks}");
        Line($"  process CPU {cpu / wall * 100:0.0}% of one core ({cpu / wall * 100 / Environment.ProcessorCount:0.0}% of {Environment.ProcessorCount} logical CPUs); decode thread {decodeThread / wall * 100:0.0}% of one core");
        Line($"  decode+present per frame: median {costs[costs.Count / 2]:0.00} ms, p95 {costs[(int)(costs.Count * 0.95)]:0.00} ms, max {costs[^1]:0.00} ms");

        // On-screen checks: the stream shows, the idle fallback uncovers the RFB picture, the next frame brings the stream back.
        // Each reads the window's centre twice: as DWM composes it (PrintWindow, which sees the swap chain but renders
        // WPF content black) and from the screen (GDI, which an overlay such as Click to Do freezes). The surface's own
        // size says whether the swap chain window covers the picture. A colour neither read can see is "unverified".
        bool gpu = Mode != "before";
        Thread.Sleep(300);
        var onStream = Probe(hwnd); string surface1 = Surface;
        string streamShown = Verdict(view.IsExternalShown && (!gpu || SurfaceSize == (Width, Height)), onStream, IsStreamColour);
        view.ShowFramebuffer();
        Thread.Sleep(400);
        var onRfb = Probe(hwnd); string surface2 = Surface;
        string rfbShown = Verdict(!view.IsExternalShown && (!gpu || SurfaceSize == (0, 0)) && !onRfb.Any(IsStreamColour), onRfb, IsFramebufferColour);
        next = clock.ElapsedTicks;
        PlayFor(0.5, null);
        Thread.Sleep(300);
        var back = Probe(hwnd); string surface3 = Surface;
        string streamBack = Verdict(view.IsExternalShown && (!gpu || SurfaceSize == (Width, Height)), back, IsStreamColour);
        var shot = view.Dispatcher.Invoke(() => view.Snapshot() is { } b ? (b.PixelWidth, b.PixelHeight) : ((int, int)?)null);
        bool snapshotOk = shot == (Width, Height);
        Line($"  stream shown: {Describe(onStream)}, surface {surface1} -> {streamShown}");
        Line($"  after ShowFramebuffer (idle fallback): {Describe(onRfb)}, surface {surface2} -> {rfbShown}");
        Line($"  next frames: {Describe(back)}, surface {surface3} -> {streamBack}; snapshot {shot?.Item1}x{shot?.Item2} {(snapshotOk ? "ok" : "WRONG")}");
        bool checks = !new[] { streamShown, rfbShown, streamBack }.Any(v => v.StartsWith("WRONG")) && snapshotOk;

        // Stress: resize the window every 25 ms while frames keep coming (UI-thread ResizeBuffers against decode-thread Present).
        var window = BenchWindowObject;
        var dpi = window.Dispatcher.Invoke(() => VisualTreeHelper.GetDpi(window));
        int presentedBefore = fallbacks;
        bool stop = false;
        int resizes = 0;
        var resizer = Task.Run(async () =>
        {
            for (int i = 0; !Volatile.Read(ref stop); i++)
            {
                int k = i;
                await window.Dispatcher.InvokeAsync(() => { window.Width = (Width - (k % 3) * 300) / dpi.DpiScaleX; window.Height = (Height - (k % 2) * 200) / dpi.DpiScaleY; });
                resizes++;
                await Task.Delay(25);
            }
        });
        next = clock.ElapsedTicks;
        var resizeWatch = Stopwatch.StartNew();
        PlayFor(2.0, null);
        Volatile.Write(ref stop, true);
        bool resizerDone = resizer.Wait(3000);
        window.Dispatcher.Invoke(() => { window.Width = Width / dpi.DpiScaleX; window.Height = Height / dpi.DpiScaleY; });
        next = clock.ElapsedTicks;
        PlayFor(0.5, null);
        Thread.Sleep(200);
        var afterResize = SurfaceSize;
        bool resizeOk = resizerDone && resizeWatch.Elapsed.TotalSeconds < 4 && fallbacks == presentedBefore && view.IsExternalShown
            && (Mode == "before" || (afterResize == (Width, Height) && !view.GpuVideoStatus.Contains("stopped")));
        Line($"  resize stress: {resizes} resizes during 2 s of frames, surface {Surface} after, {view.GpuVideoStatus.Split(';')[0]} -> {(resizeOk ? "ok" : "WRONG")}");

        // 1:1 in a smaller window, scrolled: the surface must cover the scroll viewport only, not the whole picture.
        window.Dispatcher.Invoke(() =>
        {
            view.ScaleMode = BetterVnc.Viewer.Core.Models.ScaleMode.OneToOne;
            Scroller.HorizontalScrollBarVisibility = Scroller.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            window.Width = Width * 2 / 3 / dpi.DpiScaleX;
            window.Height = Height * 2 / 3 / dpi.DpiScaleY;
        });
        Thread.Sleep(200);
        window.Dispatcher.Invoke(() => { Scroller.ScrollToHorizontalOffset(300 / dpi.DpiScaleX); Scroller.ScrollToVerticalOffset(200 / dpi.DpiScaleY); });
        next = clock.ElapsedTicks;
        PlayFor(0.5, null);
        Thread.Sleep(200);
        var viewport = window.Dispatcher.Invoke(() => ((int)Math.Round(Scroller.ViewportWidth * dpi.DpiScaleX), (int)Math.Round(Scroller.ViewportHeight * dpi.DpiScaleY),
            Scroller.HorizontalOffset * dpi.DpiScaleX, Scroller.VerticalOffset * dpi.DpiScaleY));
        var scrolled = SurfaceSize;
        bool clipOk = Mode == "before" || (scrolled is { } sz && Math.Abs(sz.Item1 - viewport.Item1) <= 1 && Math.Abs(sz.Item2 - viewport.Item2) <= 1);
        Line($"  1:1 scrolled by {viewport.Item3:0},{viewport.Item4:0}: viewport {viewport.Item1}x{viewport.Item2}, surface {Surface} -> {(clipOk ? "ok" : "WRONG")}");
        return checks && resizeOk && clipOk ? 0 : 3;

    }

    /// <summary>"ok" when the state is right and a live read shows the expected colour; "unverified" when no read is live.</summary>
    private static string Verdict(bool state, (byte R, byte G, byte B)[] reads, Func<(byte R, byte G, byte B), bool> expected)
    {
        if (!state) return "WRONG (state)";
        if (reads.Any(expected)) return "ok";
        return reads.All(c => c == (0, 0, 0)) ? "state ok, colour unverified (no live screen read)" : "WRONG (colour)";
    }

    private static (int, int)? SurfaceSize
    {
        get
        {
            var child = FindWindowEx(BenchWindow, IntPtr.Zero, "BetterVncVideoSurface", null);
            if (child == IntPtr.Zero) return null;
            GetWindowRect(child, out var r);
            return (r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    private static string Describe((byte R, byte G, byte B)[] c) => $"PrintWindow rgb({c[0].R},{c[0].G},{c[0].B}), screen rgb({c[1].R},{c[1].G},{c[1].B})";
    private static IntPtr BenchWindow;
    private static string Surface
    {
        get
        {
            var child = FindWindowEx(BenchWindow, IntPtr.Zero, "BetterVncVideoSurface", null);
            if (child == IntPtr.Zero) return "none";
            GetWindowRect(child, out var r);
            return $"{r.Right - r.Left}x{r.Bottom - r.Top}{(IsWindowVisible(child) ? "" : " hidden")}";
        }
    }
    private static bool IsStreamColour((byte R, byte G, byte B) c) => c.R > 180 && c.G < 80 && c.B < 80;
    private static bool IsFramebufferColour((byte R, byte G, byte B) c) => c.B > 100 && c.R < 40 && c.G < 40;

    /// <summary>The colour at the window's centre: [0] as DWM renders the window (PrintWindow, full content), [1] on screen.</summary>
    private static unsafe (byte R, byte G, byte B)[] Probe(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var result = new (byte, byte, byte)[2];
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(mem, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, bitmap);
        if (PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */))
        {
            byte* p = (byte*)bits + ((long)(h / 2) * w + w / 2) * 4;
            result[0] = (p[2], p[1], p[0]);
        }
        SelectObject(mem, old);
        DeleteObject(bitmap);
        DeleteDC(mem);
        uint c = GetPixel(screen, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        ReleaseDC(IntPtr.Zero, screen);
        result[1] = ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        return result;
    }

    // ------------------------------------------------------------------ content

    /// <summary>Five seconds of screen-like content (scrolling text-like detail, a sweeping box, a fixed red centre square), encoded once.</summary>
    private static List<EncodedFrame> EncodeStream()
    {
        int count = Fps * 5;
        using var encoder = new H264Encoder(Width, Height, Fps, Kbps, preferHardware: true);
        var frames = new List<EncodedFrame>();
        var bgra = new byte[Width * Height * 4];
        for (int t = 0; t < count; t++)
        {
            Render(bgra, t);
            var deadline = Environment.TickCount64 + 2000;
            while (!encoder.WantsInput)
            {
                encoder.Pump(frames);
                if (Environment.TickCount64 > deadline) throw new TimeoutException("encoder never asked for input");
                Thread.Sleep(1);
            }
            if (t == 0) encoder.ForceKeyframe();
            encoder.Encode(bgra, Width * 4, t * 1_000_000L / Fps);
            encoder.Pump(frames);
        }
        encoder.Drain(frames);
        if (!frames[0].Keyframe) throw new InvalidOperationException("the stream does not start with a key frame");
        long bytes = frames.Sum(f => (long)f.Data.Length);
        Line($"stream: {frames.Count} frames from {encoder.Name}, {bytes * 8.0 / 5 / 1e6:0.0} Mbit/s actual");
        return frames;
    }

    private static void Render(byte[] bgra, int t)
    {
        int w = Width, h = Height;
        Parallel.For(0, h, y =>
        {
            int scrolled = y + t * 3;
            int row = scrolled / 14, inRow = scrolled % 14;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int col = x / 9;
                bool glyph = inRow < 10 && x % 9 < 7 && ((Hash(row, col) & 3) != 0) && ((Hash(row * 7 + inRow, x) & 1) == 0);
                byte b = (byte)(200 + y * 40 / h), g = (byte)(205 + x * 30 / w), r = (byte)(210);
                if (glyph) { b = 40; g = 40; r = 50; }
                int bx = (t * 8) % (w + 300) - 300;
                if (x >= bx && x < bx + 300 && y >= h / 6 && y < h / 6 + 200) { b = 230; g = 120; r = 40; }
                if (Math.Abs(x - w / 2) < 120 && Math.Abs(y - h / 2) < 120) { b = 30; g = 20; r = 230; }
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
        });
    }

    private static int Hash(int a, int b) => (int)((uint)(a * 73856093 ^ b * 19349663) >> 7);

    // ------------------------------------------------------------------ fake VNC server: one dark-blue picture, then silence

    private static int StartFakeServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(() =>
        {
            using var tcp = listener.AcceptTcpClient();
            tcp.NoDelay = true;
            using var stream = tcp.GetStream();
            var r = new RfbReader(stream);
            stream.Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            r.ReadString(12);
            stream.Write(new byte[] { 1, SecurityType.None });
            r.ReadU8();
            stream.Write(new byte[] { 0, 0, 0, 0 });
            r.ReadU8();
            var init = new RfbMessageBuilder();
            init.U16((ushort)Width).U16((ushort)Height);
            BetterVnc.Protocol.PixelFormat.Bgrx32.Write(init);
            var name = Encoding.UTF8.GetBytes("stream bench");
            init.U32((uint)name.Length).Bytes(name);
            stream.Write(init.Data);
            bool sent = false;
            try
            {
                while (true)
                {
                    byte type = r.ReadU8();
                    switch (type)
                    {
                        case ClientMessage.SetPixelFormat: r.Skip(3); BetterVnc.Protocol.PixelFormat.Read(r); break;
                        case ClientMessage.SetEncodings: r.Skip(1); int n = r.ReadU16(); for (int i = 0; i < n; i++) r.ReadS32(); break;
                        case ClientMessage.FramebufferUpdateRequest:
                            r.ReadBytes(9);
                            if (sent) break; // the picture never changes: later requests stay pending, like a still desktop
                            sent = true;
                            var pixels = new byte[Width * Height * 4];
                            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = FbB; pixels[i + 1] = FbG; pixels[i + 2] = FbR; }
                            var b = new RfbMessageBuilder();
                            b.U8(ServerMessage.FramebufferUpdate).Pad(1).U16(1);
                            b.U16(0).U16(0).U16((ushort)Width).U16((ushort)Height).S32(RfbEncoding.Raw).Bytes(pixels);
                            stream.Write(b.Data);
                            break;
                        case ClientMessage.KeyEvent: r.ReadBytes(7); break;
                        case ClientMessage.PointerEvent: r.ReadBytes(5); break;
                        case ClientMessage.ClientCutText: r.Skip(3); int len = r.ReadS32(); r.Skip(Math.Abs(len)); break;
                        case ClientMessage.Qemu:
                            byte sub = r.ReadU8();
                            if (sub == QemuMessage.ExtendedKeyEvent) { r.ReadU16(); r.ReadU32(); r.ReadU32(); }
                            else { ushort op = r.ReadU16(); if (op == QemuMessage.AudioSetFormat) r.ReadBytes(6); }
                            break;
                        default: return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException) { }
        }) { IsBackground = true, Name = "fake VNC server" }.Start();
        return port;
    }

    // ------------------------------------------------------------------ native

    private static TimeSpan ThreadCpu()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user);
        return TimeSpan.FromTicks(kernel + user);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
}
