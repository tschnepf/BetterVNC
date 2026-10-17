using System.Drawing;

namespace BetterVnc.Agent;

/// <summary>The BetterVNC icon, embedded from the viewer's assets, with a drawn fallback.</summary>
public static class TrayIcon
{
    private static Icon? _icon;

    public static Icon Load()
    {
        if (_icon != null) return _icon;
        try
        {
            using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream("BetterVNC.ico");
            if (s != null) return _icon = new Icon(s);
        }
        catch { }
        // Fallback: two overlapping rounded rectangles (a clipboard-ish glyph) in the accent colour.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(79, 140, 255));
            using var front = new SolidBrush(Color.White);
            g.FillRectangle(back, 4, 4, 18, 22);
            g.FillRectangle(front, 10, 10, 18, 18);
            g.FillRectangle(back, 13, 13, 12, 12);
        }
        return _icon = Icon.FromHandle(bmp.GetHicon());
    }
}
