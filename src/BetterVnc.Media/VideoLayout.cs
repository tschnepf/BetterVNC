namespace BetterVnc.Media;

/// <summary>A rectangle in device pixels; edges may be fractional.</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => !(Width > 0 && Height > 0);

    public PixelRect Intersect(PixelRect other)
    {
        double left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        double right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }

    /// <summary>The same rectangle with every edge on the nearest whole pixel.</summary>
    public PixelRect Snapped()
    {
        double left = Math.Round(X), top = Math.Round(Y);
        return new PixelRect(left, top, Math.Round(Right) - left, Math.Round(Bottom) - top);
    }
}

/// <summary>
/// Where the swap-chain window goes and where the picture lands inside it. <see cref="X"/>..<see cref="Height"/> is the
/// window, in device pixels of its parent's client area; <see cref="Destination"/> is the picture relative to the window
/// (it reaches past the window's edges when part of the picture is scrolled away or covered).
/// </summary>
public readonly record struct VideoPlacement(int X, int Y, int Width, int Height, PixelRect Destination)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class VideoLayout
{
    /// <summary>
    /// Places a streamed picture over the framebuffer picture. <paramref name="image"/> is where the framebuffer
    /// (<paramref name="fbWidth"/>×<paramref name="fbHeight"/>) is drawn, already fitted or 1:1, in device pixels.
    /// Without <paramref name="offset"/> the frame shows the whole desktop and covers <paramref name="image"/>; with one it
    /// shows the part of the desktop whose top-left corner is at that framebuffer position (the primary display of a
    /// desktop too large to encode) and is scaled like the framebuffer, so it lands exactly on that part of the picture,
    /// clipped to the framebuffer. <paramref name="clip"/> is what is visible of the view (the scroll viewport).
    /// Edges are snapped to whole pixels, so a 1:1 picture maps one texel to one pixel.
    /// </summary>
    public static VideoPlacement Place(PixelRect image, int fbWidth, int fbHeight, int frameWidth, int frameHeight, (int X, int Y)? offset, PixelRect clip)
    {
        if (image.IsEmpty || frameWidth <= 0 || frameHeight <= 0) return default;
        var dest = image;
        if (offset is { } at)
        {
            if (fbWidth <= 0 || fbHeight <= 0) return default;
            double sx = image.Width / fbWidth, sy = image.Height / fbHeight;
            dest = new PixelRect(image.X + at.X * sx, image.Y + at.Y * sy, frameWidth * sx, frameHeight * sy);
        }
        dest = dest.Snapped();
        var window = dest.Intersect(image.Snapped()).Intersect(clip.Snapped());
        if (window.IsEmpty) return default;
        return new VideoPlacement((int)window.X, (int)window.Y, (int)window.Width, (int)window.Height,
            new PixelRect(dest.X - window.X, dest.Y - window.Y, dest.Width, dest.Height));
    }
}
