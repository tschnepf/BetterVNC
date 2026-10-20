using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BetterVnc.AvaloniaSpike;

internal static class Program
{
    private static string[] Args = Array.Empty<string>();
    private static string? Opt(string name) { int i = Array.IndexOf(Args, name); return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null; }
    private static (int W, int H) OptSize(string name, (int, int) fallback) =>
        Opt(name)?.Split('x') is [var w, var h] && int.TryParse(w, out var iw) && int.TryParse(h, out var ih) ? (iw, ih) : fallback;

    [STAThread]
    private static int Main(string[] args)
    {
        Args = args;
        Run.timeBeginPeriod(1);
        var (fbW, fbH) = OptSize("--size", (1920, 1080));
        bool full = Opt("--dirty") == "full";
        int seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 8;
        var (winW, winH) = OptSize("--win", (1280, 720));

        var fb = new SyntheticFramebuffer(fbW, fbH);
        var bitmap = new WriteableBitmap(fbW, fbH, 96, 96, PixelFormats.Bgr32, null);
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Title = "fb test (WPF)", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false,
            ShowInTaskbar = false, Background = Brushes.Black, Content = image, Width = 100, Height = 100, Left = 0, Top = 0,
        };
        var run = new Run($"fb wpf {fbW}x{fbH} dirty={(full ? "full" : "small")} shown in {winW}x{winH}", Run.GetCurrentThreadId());
        var dirty = new List<DirtyRect>();
        int frame = 0;
        bool queued = false;

        unsafe void Render()
        {
            long t0 = Stopwatch.GetTimestamp();
            DirtyRect[] rects;
            int f;
            lock (dirty) { rects = dirty.ToArray(); dirty.Clear(); f = frame; queued = false; }
            bitmap.Lock();
            try
            {
                lock (fb.SyncRoot)
                    fixed (byte* src = fb.Pixels)
                        foreach (var r in rects)
                        {
                            int rowBytes = r.Width * 4;
                            for (int y = 0; y < r.Height; y++)
                                Buffer.MemoryCopy(src + ((r.Y + y) * fb.Width + r.X) * 4, (byte*)bitmap.BackBuffer + (r.Y + y) * bitmap.BackBufferStride + r.X * 4, rowBytes, rowBytes);
                            bitmap.AddDirtyRect(new Int32Rect(r.X, r.Y, r.Width, r.Height));
                        }
            }
            finally { bitmap.Unlock(); }
            run.UiCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            run.Submit(f & 0xFFFF);
        }

        void Queue(List<DirtyRect> rects, int f)
        {
            lock (dirty)
            {
                dirty.AddRange(rects);
                frame = f;
                if (queued) return;
                queued = true;
            }
            window.Dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
        }

        window.Loaded += async (_, _) =>
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var area = SystemParameters.WorkArea; // DIPs of the primary display
                window.Width = winW / dpi.DpiScaleX;
                window.Height = winH / dpi.DpiScaleY;
                window.Left = area.Right - window.Width - 16 / dpi.DpiScaleX;
                window.Top = area.Bottom - window.Height - 16 / dpi.DpiScaleY;
                await Task.Delay(500);
                var o = image.PointToScreen(new Point(0, 0));
                var origin = ((int)Math.Round(o.X), (int)Math.Round(o.Y));
                double scale = (double)winW / fbW;
                (int, int)[] points = Enumerable.Range(0, Marker.Blocks)
                    .Select(k => (origin.Item1 + (int)Math.Round((k * fb.Block + fb.Block / 2.0) * scale), origin.Item2 + (int)Math.Round(fb.Block / 2.0 * scale))).ToArray();
                using var probe = ScreenProbe.Create(origin.Item1 + 1, origin.Item2 + 1);
                probe.Watch(points);
                probe.Start();
                fb.Start(full, Queue);
                await Task.Delay(2000);
                run.Begin();
                probe.Watch(points);
                await Task.Delay(seconds * 1000);
                fb.Stop();
                await Task.Delay(300);
                Console.WriteLine(run.End(probe, "WPF Image + WriteableBitmap (Bgr32), dirty rects copied with AddDirtyRect (as VncView.Render)"));
            }
            catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return 0;
    }
}
