using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterVnc.IconGen;

/// <summary>
/// The BetterVNC icon: a dark rounded tile (the app's dark theme), a remote screen outlined in the
/// accent blue, and a white local pointer that reaches past the screen edge, the one thing that
/// sets BetterVNC apart from a browser console: the cursor is yours, not the server's.
/// All geometry is in a 256 x 256 box; the same path strings feed the SVG and the WPF renderer.
/// </summary>
internal static class Program
{
    // Theme colours (src/BetterVnc.App/Themes/Dark.xaml).
    private const string Bg = "#14161A";
    private const string Card = "#23272F";
    private const string Border = "#343A45";
    private const string Accent = "#4F8CFF";
    private const string AccentHover = "#6B9DFF";
    private const string Screen = "#243049";
    private const string White = "#FFFFFF";

    private sealed record Shape(string Path, string? Fill, string? Stroke, double StrokeWidth, string? FillGradient = null);

    [STAThread]
    private static void Main()
    {
        var root = FindRepoRoot();
        var assets = Path.Combine(root, "src", "BetterVnc.App", "Assets");
        Directory.CreateDirectory(assets);

        File.WriteAllText(Path.Combine(assets, "icon.svg"), ToSvg(BuildShapes(1.0)), new UTF8Encoding(false));

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = new List<(int Size, byte[] Png, byte[] Bgra)>();
        foreach (var size in sizes)
        {
            // Small sizes get a bigger pointer and a heavier screen outline so the glyph survives 1 px strokes.
            var emphasis = size <= 24 ? 1.45 : size <= 48 ? 1.2 : 1.0;
            var bmp = Render(BuildShapes(emphasis), size);
            frames.Add((size, EncodePng(bmp), ToBgra(bmp)));
        }

        File.WriteAllBytes(Path.Combine(assets, "BetterVNC.ico"), BuildIco(frames));
        File.WriteAllBytes(Path.Combine(root, "docs", "icon.png"), frames.Single(f => f.Size == 256).Png);

        Console.WriteLine($"Wrote {Path.Combine(assets, "BetterVNC.ico")} ({sizes.Length} sizes), icon.svg, docs/icon.png");
    }

    private static List<Shape> BuildShapes(double pointerEmphasis)
    {
        var shapes = new List<Shape>
        {
            // Tile with a subtle vertical gradient and a hairline border so it reads on both light and dark surfaces.
            new(RoundedRect(0, 0, 256, 256, 56), Bg, null, 0, FillGradient: "tile"),
            new(RoundedRect(4, 4, 248, 248, 52), null, Border, 3),
            // Remote screen: slightly raised above centre to leave room for the pointer.
            new(RoundedRect(40, 46, 176, 122, 18), Screen, Accent, 16 * Math.Max(1.0, pointerEmphasis * 0.9)),
        };

        // Two short lines in the top-left of the screen stand in for remote content; below 32 px they are
        // only noise, so the small frames leave them out.
        if (pointerEmphasis < 1.4)
        {
            shapes.Add(new(RoundedRect(64, 72, 72, 10, 5), AccentHover, null, 0));
            shapes.Add(new(RoundedRect(64, 94, 48, 10, 5), Border, null, 0));
        }

        // Pointer: classic arrow, tip at (120, 84), scaled by 6 (times emphasis for small sizes).
        double s = 6 * pointerEmphasis, tx = 120, ty = 84;
        (double X, double Y)[] pts = [(0, 0), (0, 16), (4.5, 12.5), (7, 18), (10, 16.5), (7.5, 11), (12, 11)];
        var sb = new StringBuilder();
        for (var i = 0; i < pts.Length; i++)
        {
            sb.Append(i == 0 ? "M" : "L");
            sb.Append(F(tx + pts[i].X * s)).Append(',').Append(F(ty + pts[i].Y * s)).Append(' ');
        }
        sb.Append('Z');
        shapes.Add(new(sb.ToString(), White, Bg, 10));
        return shapes;
    }

    private static string RoundedRect(double x, double y, double w, double h, double r) =>
        $"M{F(x + r)},{F(y)} H{F(x + w - r)} A{F(r)},{F(r)} 0 0 1 {F(x + w)},{F(y + r)} V{F(y + h - r)} " +
        $"A{F(r)},{F(r)} 0 0 1 {F(x + w - r)},{F(y + h)} H{F(x + r)} A{F(r)},{F(r)} 0 0 1 {F(x)},{F(y + h - r)} " +
        $"V{F(y + r)} A{F(r)},{F(r)} 0 0 1 {F(x + r)},{F(y)} Z";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ToSvg(List<Shape> shapes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\">");
        sb.AppendLine("  <defs>");
        sb.AppendLine($"    <linearGradient id=\"tile\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{Card}\"/><stop offset=\"1\" stop-color=\"{Bg}\"/></linearGradient>");
        sb.AppendLine("  </defs>");
        foreach (var s in shapes)
        {
            var fill = s.FillGradient is not null ? $"url(#{s.FillGradient})" : s.Fill ?? "none";
            var stroke = s.Stroke is null ? "" : $" stroke=\"{s.Stroke}\" stroke-width=\"{F(s.StrokeWidth)}\" stroke-linejoin=\"round\"";
            sb.AppendLine($"  <path d=\"{s.Path}\" fill=\"{fill}\"{stroke}/>");
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static RenderTargetBitmap Render(List<Shape> shapes, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            foreach (var s in shapes)
            {
                Brush? fill = s.FillGradient is not null
                    ? new LinearGradientBrush(ParseColor(Card), ParseColor(Bg), 90)
                    : s.Fill is null ? null : new SolidColorBrush(ParseColor(s.Fill));
                Pen? pen = s.Stroke is null ? null : new Pen(new SolidColorBrush(ParseColor(s.Stroke)), s.StrokeWidth) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(fill, pen, Geometry.Parse(s.Path));
            }
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] ToBgra(BitmapSource bmp)
    {
        var straight = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        straight.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// ICO with uncompressed 32-bit DIB frames up to 64 px (what every Windows version and shell
    /// extension reads) and PNG frames above that to keep the file small.
    /// </summary>
    private static byte[] BuildIco(List<(int Size, byte[] Png, byte[] Bgra)> frames)
    {
        var images = frames.Select(f => (f.Size, Data: f.Size <= 64 ? ToDib(f.Size, f.Bgra) : f.Png)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size == 256 ? 0 : size)); w.Write((byte)(size == 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
        return ms.ToArray();
    }

    private static byte[] ToDib(int size, byte[] bgra)
    {
        var stride = size * 4;
        var maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(0); w.Write(stride * size + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (var y = size - 1; y >= 0; y--) w.Write(bgra, y * stride, stride);   // XOR image, bottom-up
        w.Write(new byte[maskStride * size]);                                       // AND mask: alpha does the work
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterVNC.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("BetterVNC.slnx not found above " + AppContext.BaseDirectory);
    }
}
