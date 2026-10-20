using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace BetterVnc.AvaloniaSpike;

// Usage: dotnet BetterVnc.AvaloniaSpike.dll <mode> [options]
//   fb     --size WxH --dirty small|full [--draw bitmap|gl] [--seconds 10] [--win WxH]   framebuffer drawing (question 1)
//   video  --path bitmap|sw-swapchain|dxva-swapchain|composition [--size WxH] [--seconds 10] [--kbps 8000]   (question 2)
//   host   [--size WxH]       NativeControlHost placement: overlay, scroll clipping, resize (question 2)
//   keys                      key mapping table and WndProc hook behaviour (question 3)
//   ui     [--seconds 60]     a window with ordinary controls for the UI Automation check (question 4)
//   api    <type names>       dumps Avalonia API signatures (development aid)
// Test windows are borderless, topmost and never activated, at the bottom right of the primary display.
internal sealed class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal static class Program
{
    public static string[] Args = Array.Empty<string>();
    public static int UiThreadId;

    public static string? Opt(string name) { int i = Array.IndexOf(Args, name); return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null; }
    public static int OptInt(string name, int fallback) => int.TryParse(Opt(name), out var v) ? v : fallback;
    public static (int W, int H) OptSize(string name, (int, int) fallback) =>
        Opt(name)?.Split('x') is [var w, var h] && int.TryParse(w, out var iw) && int.TryParse(h, out var ih) ? (iw, ih) : fallback;

    [STAThread]
    private static int Main(string[] args)
    {
        Args = args;
        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "api") return DumpApi(args.Skip(1).ToArray());
        Run.timeBeginPeriod(1);
        UiThreadId = Run.GetCurrentThreadId();
        // --composition winui|dcomp|lowlatency|redirection picks how Avalonia's window reaches DWM (default: Avalonia's choice).
        var builder = AppBuilder.Configure<SpikeApp>().UsePlatformDetect().LogToTrace();
        if (Opt("--composition") is { } composition)
            builder = builder.With(new Win32PlatformOptions
            {
                ShouldRenderOnUIThread = Args.Contains("--ui-render"),
                CompositionMode = new[]
                {
                    composition switch
                    {
                        "winui" => Win32CompositionMode.WinUIComposition,
                        "dcomp" => Win32CompositionMode.DirectComposition,
                        "lowlatency" => Win32CompositionMode.LowLatencyDxgiSwapChain,
                        "redirection" => Win32CompositionMode.RedirectionSurface,
                        _ => throw new ArgumentException("--composition winui|dcomp|lowlatency|redirection"),
                    },
                },
            });
        builder.SetupWithoutStarting();
        Func<Task<int>> test = mode switch
        {
            "fb" => FramebufferTest.RunAsync,
            "video" => VideoTest.RunAsync,
            "host" => HostTest.RunAsync,
            "keys" => KeyTest.RunAsync,
            "ui" => UiTest.RunAsync,
            _ => () => { Console.Error.WriteLine("modes: fb, video, host, keys, ui, api"); return Task.FromResult(2); },
        };
        int exit = 1;
        var cts = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { exit = await test(); }
            catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
            finally { cts.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(cts.Token);
        return exit;
    }

    /// <summary>
    /// A borderless, topmost window that is never activated, with a client area of <paramref name="width"/> x
    /// <paramref name="height"/> device pixels at the bottom right of the primary display. Shown before it returns.
    /// </summary>
    public static async Task<Window> ShowTestWindowAsync(string title, Control content, int width, int height)
    {
        var window = new Window
        {
            Title = title, Content = content, Topmost = true, ShowActivated = false, ShowInTaskbar = false, CanResize = false,
            WindowDecorations = WindowDecorations.None, Background = Brushes.Black, Width = 100, Height = 100,
        };
        var screen = window.Screens.Primary ?? throw new InvalidOperationException("no primary screen");
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        window.Width = width / scale;
        window.Height = height / scale;
        window.Position = new PixelPoint(area.Right - width - 16, area.Bottom - height - 16);
        var opened = new TaskCompletionSource();
        window.Opened += (_, _) => opened.TrySetResult();
        window.Show();
        await opened.Task;
        await Task.Delay(300); // first layout and render
        if (Math.Abs(window.RenderScaling - scale) > 0.001)
        {
            window.Width = width / window.RenderScaling;
            window.Height = height / window.RenderScaling;
            await Task.Delay(200);
        }
        return window;
    }

    /// <summary>The device-pixel screen position of a point in <paramref name="control"/>'s coordinates.</summary>
    public static (int X, int Y) ScreenPoint(Control control, Point p)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var inTop = control.TranslatePoint(p, top) ?? p;
        var px = top.PointToScreen(inTop);
        return (px.X, px.Y);
    }

    /// <summary>Screen points of the marker's 18 block centres for a picture shown at <paramref name="origin"/> with <paramref name="scale"/> device pixels per picture pixel.</summary>
    public static (int X, int Y)[] MarkerPoints((int X, int Y) origin, int block, double scale) =>
        Enumerable.Range(0, Marker.Blocks)
            .Select(k => (origin.X + (int)Math.Round((k * block + block / 2.0) * scale), origin.Y + (int)Math.Round(block / 2.0 * scale)))
            .ToArray();

    private static int DumpApi(string[] typeNames)
    {
        var asms = new[] { typeof(Visual).Assembly, typeof(Window).Assembly, typeof(Avalonia.Win32.Input.KeyInterop).Assembly,
            typeof(Avalonia.OpenGL.GlInterface).Assembly };
        foreach (var name in typeNames)
        {
            var t = asms.Select(a => a.GetTypes().FirstOrDefault(x => x.FullName == name || x.Name == name)).FirstOrDefault(x => x != null);
            if (t == null) { Console.WriteLine($"== {name}: not found"); continue; }
            Console.WriteLine($"== {t.FullName} ({(t.IsPublic || t.IsNestedPublic ? "public" : "internal")} {(t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class")})");
            if (t.IsEnum) { Console.WriteLine("   " + string.Join(", ", Enum.GetNames(t))); continue; }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMembers(flags).OrderBy(m => m.Name))
            {
                string s = m switch
                {
                    MethodInfo mi when !mi.IsSpecialName => $"{(mi.IsStatic ? "static " : "")}{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                    ConstructorInfo ci => $"ctor({string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                    PropertyInfo pi => $"prop {pi.PropertyType.Name} {pi.Name}",
                    FieldInfo fi => $"field {fi.FieldType.Name} {fi.Name}",
                    EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
                    _ => "",
                };
                if (s.Length > 0) Console.WriteLine("   " + s);
            }
        }
        return 0;
    }
}
