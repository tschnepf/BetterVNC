using Avalonia;
using BetterVnc.Media;

namespace BetterVnc.Viewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Where the picture cannot go through the GPU swap chain it is drawn with OpenGL inside Avalonia's compositor;
        // DirectComposition with rendering on the UI thread cuts a display frame of latency there (docs/AVALONIA-SPIKE.md).
        if (PictureNeedsComposition())
            builder = builder.With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.DirectComposition, Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface },
                ShouldRenderOnUIThread = true,
            });
        return builder;
    }

    /// <summary>True when the picture will not have the swap chain: asked for (BETTERVNC_PICTURE), or no Direct3D 11 GPU.</summary>
    private static bool PictureNeedsComposition()
    {
        var wanted = Environment.GetEnvironmentVariable("BETTERVNC_PICTURE")?.Trim().ToLowerInvariant();
        if (wanted is "gl" or "opengl" or "bitmap") return true;
        try
        {
            D3D11VideoDevice.Create().Dispose();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
