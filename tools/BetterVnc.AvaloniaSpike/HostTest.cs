using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Question 2, placement: does a NativeControlHost child window (the swap chain) sit where VideoSurfaceHost puts it
/// today? Checks the child window's rectangle and region against the control (filling the window), against a
/// ScrollViewer viewport at 1:1 when scrolled, while hidden, after resizes; and whether an Avalonia element drawn over
/// it shows (airspace). Rectangles come from GetWindowRect/GetWindowRgnBox; colours from the screen (GDI), which only
/// says something while the PC is unlocked.
/// </summary>
internal static class HostTest
{
    public static async Task<int> RunAsync()
    {
        var (w, h) = Program.OptSize("--size", (1280, 720));
        var view = new SwapChainVideoView { Width = double.NaN, Height = double.NaN };
        var overlay = new Border { Background = Brushes.Lime, Width = 120, Height = 120, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
        var grid = new Grid();
        grid.Children.Add(view);
        grid.Children.Add(overlay);
        var window = await Program.ShowTestWindowAsync("host test", grid, w, h);
        double s = window.RenderScaling;
        await view.InitAsync();
        var red = new byte[w * h * 4];
        for (int i = 0; i < red.Length; i += 4) { red[i + 2] = 230; red[i + 3] = 255; }
        void Show() { if (!view.Present(red, w, h)) Console.WriteLine("  present refused: " + view.Renderer.Failure); }
        Show();
        await Task.Delay(300);
        bool ok = true;
        string Check(string what, bool good) { ok &= good; return $"  {what}: {(good ? "ok" : "WRONG")}"; }
        Native.RECT Rect() { Native.GetWindowRect(view.Hwnd, out var r); return r; }
        string Region() => Native.GetWindowRgnBox(view.Hwnd, out var b) switch { 0 => "none", var k => $"{b} (type {k})" };
        var origin = Program.ScreenPoint(view, new Point(0, 0));

        // 1. Filling the window.
        var r1 = Rect();
        Console.WriteLine($"[host] child window {r1}, region {Region()}, visible {Native.IsWindowVisible(view.Hwnd)}; control at {origin.X},{origin.Y} {w}x{h}");
        Console.WriteLine(Check("child covers the control", r1.Left == origin.X && r1.Top == origin.Y && r1.Right - r1.Left == w && r1.Bottom - r1.Top == h));
        var c = Native.ScreenColor(origin.X + w / 2, origin.Y + h / 2);
        Console.WriteLine($"  screen at centre rgb({c.R},{c.G},{c.B}) (red expected while unlocked)");

        // 2. An Avalonia element over the video (airspace).
        overlay.IsVisible = true;
        await Task.Delay(300);
        Show();
        await Task.Delay(200);
        var oc = Native.ScreenColor(origin.X + w - (int)(60 * s), origin.Y + h - (int)(60 * s));
        Console.WriteLine($"  overlay corner rgb({oc.R},{oc.G},{oc.B}): {(oc.G > 200 && oc.R < 80 ? "Avalonia overlay visible" : oc.R > 180 ? "video covers the overlay (airspace)" : "no live screen read")}");
        overlay.IsVisible = false;

        // 3. Hidden control.
        view.IsVisible = false;
        await Task.Delay(300);
        var r3 = Rect();
        Console.WriteLine(Check($"hidden: child visible={Native.IsWindowVisible(view.Hwnd)} rect {r3}", !Native.IsWindowVisible(view.Hwnd) || r3.Right - r3.Left == 0));
        view.IsVisible = true;
        await Task.Delay(300);
        Show();

        // 4. Resizes while frames keep coming.
        for (int i = 0; i < 40; i++)
        {
            window.Width = (w - (i % 3) * 200) / s;
            window.Height = (h - (i % 2) * 120) / s;
            await Task.Delay(25);
            Show();
        }
        window.Width = w / s; window.Height = h / s;
        await Task.Delay(400);
        Show();
        var r4 = Rect();
        Console.WriteLine(Check($"after 40 resizes: child {r4}, renderer usable {view.Renderer.IsUsable}", r4.Right - r4.Left == w && r4.Bottom - r4.Top == h && view.Renderer.IsUsable));

        // 5. 1:1 inside a smaller ScrollViewer with a margin around it, scrolled.
        grid.Children.Clear();
        view.Width = w / s; view.Height = h / s;
        var scroller = new ScrollViewer
        {
            Content = view, Margin = new Thickness(100 / s), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, Background = Brushes.DimGray,
        };
        grid.Children.Add(scroller);
        await Task.Delay(400);
        scroller.Offset = new Vector(300 / s, 200 / s);
        await Task.Delay(400);
        Show();
        await Task.Delay(200);
        var vp = Program.ScreenPoint(scroller, new Point(0, 0));
        int vpW = (int)Math.Round(scroller.Viewport.Width * s), vpH = (int)Math.Round(scroller.Viewport.Height * s);
        var r5 = Rect();
        string region5 = Region();
        Console.WriteLine($"  scrolled 300,200 in a viewport at {vp.X},{vp.Y} {vpW}x{vpH}: child {r5}, region {region5}");
        bool rectInside = r5.Left >= vp.X && r5.Top >= vp.Y && r5.Right <= vp.X + vpW && r5.Bottom <= vp.Y + vpH;
        bool clippedByRegion = region5 != "none";
        Console.WriteLine(Check("child window kept inside the viewport (rect or region)", rectInside || clippedByRegion));
        var outside = Native.ScreenColor(vp.X - 50, vp.Y - 50);
        Console.WriteLine($"  screen in the margin above-left of the viewport rgb({outside.R},{outside.G},{outside.B}) (black window background expected, red = video leaks out)");

        window.Close();
        view.Dispose();
        Console.WriteLine(ok ? "[host] all placement checks ok" : "[host] some placement checks WRONG");
        return ok ? 0 : 3;
    }
}
