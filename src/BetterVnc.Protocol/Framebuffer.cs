namespace BetterVnc.Protocol;

/// <summary>Integer rectangle in framebuffer coordinates.</summary>
public readonly record struct RfbRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RfbRect Union(RfbRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x = Math.Min(X, other.X);
        int y = Math.Min(Y, other.Y);
        int r = Math.Max(Right, other.Right);
        int b = Math.Max(Bottom, other.Bottom);
        return new RfbRect(x, y, r - x, b - y);
    }

    public RfbRect Intersect(RfbRect other)
    {
        int x = Math.Max(X, other.X);
        int y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? default : new RfbRect(x, y, r - x, b - y);
    }
}

/// <summary>
/// The client-side copy of the remote screen. Pixels are stored as 32-bit B,G,R,X
/// (little-endian 0x00RRGGBB), one row after another with <see cref="Stride"/> bytes per row.
/// Decoders write into <see cref="Pixels"/> directly; consumers read it under <see cref="SyncRoot"/>
/// when a <see cref="VncClient.FramebufferUpdated"/> event fires.
/// </summary>
public sealed class Framebuffer
{
    public const int BytesPerPixel = 4;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * BytesPerPixel;
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public object SyncRoot { get; } = new();

    private readonly List<RfbRect> _dirty = new();

    public Framebuffer(int width, int height)
    {
        Resize(width, height);
    }

    public RfbRect Bounds => new(0, 0, Width, Height);

    public void Resize(int width, int height)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var old = Pixels;
        int oldW = Width, oldH = Height;
        var fresh = new byte[width * height * BytesPerPixel];
        // Preserve the overlapping region so a resize does not flash to black.
        int copyW = Math.Min(oldW, width) * BytesPerPixel;
        int copyH = Math.Min(oldH, height);
        for (int y = 0; y < copyH; y++)
            Buffer.BlockCopy(old, y * oldW * BytesPerPixel, fresh, y * width * BytesPerPixel, copyW);
        Pixels = fresh;
        Width = width;
        Height = height;
        _dirty.Clear();
        _dirty.Add(Bounds);
    }

    public int Offset(int x, int y) => (y * Width + x) * BytesPerPixel;

    public void MarkDirty(RfbRect rect)
    {
        rect = rect.Intersect(Bounds);
        if (rect.IsEmpty) return;
        // Keep a bounded list; collapse to a bounding box when it grows large.
        if (_dirty.Count >= 64)
        {
            var all = rect;
            foreach (var r in _dirty) all = all.Union(r);
            _dirty.Clear();
            _dirty.Add(all);
        }
        else
        {
            _dirty.Add(rect);
        }
    }

    /// <summary>Returns and clears the pending dirty rectangles.</summary>
    public RfbRect[] TakeDirty()
    {
        if (_dirty.Count == 0) return Array.Empty<RfbRect>();
        var arr = _dirty.ToArray();
        _dirty.Clear();
        return arr;
    }

    public void FillRect(RfbRect rect, uint bgrx)
    {
        rect = rect.Intersect(Bounds);
        if (rect.IsEmpty) return;
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
        for (int y = rect.Y; y < rect.Bottom; y++)
            span.Slice(y * Width + rect.X, rect.Width).Fill(bgrx);
    }

    public void CopyRect(int srcX, int srcY, RfbRect dst)
    {
        // Rows may overlap: copy through a temporary when moving downward.
        int rowBytes = dst.Width * BytesPerPixel;
        if (dst.X < 0 || dst.Y < 0 || dst.Right > Width || dst.Bottom > Height) return;
        if (srcX < 0 || srcY < 0 || srcX + dst.Width > Width || srcY + dst.Height > Height) return;
        if (srcY >= dst.Y)
        {
            for (int y = 0; y < dst.Height; y++)
                Buffer.BlockCopy(Pixels, Offset(srcX, srcY + y), Pixels, Offset(dst.X, dst.Y + y), rowBytes);
        }
        else
        {
            for (int y = dst.Height - 1; y >= 0; y--)
                Buffer.BlockCopy(Pixels, Offset(srcX, srcY + y), Pixels, Offset(dst.X, dst.Y + y), rowBytes);
        }
    }

    /// <summary>Copies a region out as a tightly packed BGRA buffer (alpha forced to 255).</summary>
    public byte[] CopyOut(RfbRect rect)
    {
        rect = rect.Intersect(Bounds);
        var result = new byte[rect.Width * rect.Height * BytesPerPixel];
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(Pixels, Offset(rect.X, rect.Y + y), result, y * rect.Width * BytesPerPixel, rect.Width * BytesPerPixel);
        for (int i = 3; i < result.Length; i += 4) result[i] = 255;
        return result;
    }
}
