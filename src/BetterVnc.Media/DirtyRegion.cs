namespace BetterVnc.Media;

/// <summary>A rectangle of pixels, in the coordinates of the captured picture.</summary>
public readonly record struct DirtyRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;
}

/// <summary>
/// The changed part of a picture, kept on a grid of 16×16 tiles (the H.264 macroblock size). Rectangles from Desktop
/// Duplication or from tile comparison are added as they come; overlapping ones merge, so every changed pixel is
/// copied and converted once. <see cref="GetRects"/> hands the tiles back as a few non-overlapping rectangles: the
/// runs of changed tiles along each tile row, merged with identical runs in the rows below.
/// </summary>
public sealed class DirtyRegion
{
    public const int TileSize = 16;

    private readonly byte[] _tiles;
    private readonly int _columns, _rows;
    private readonly List<DirtyRect> _rects = new();
    private int _count;
    private bool _rectsValid;
    private long _area;

    public DirtyRegion(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("The picture needs a positive size.");
        Width = width;
        Height = height;
        _columns = (width + TileSize - 1) / TileSize;
        _rows = (height + TileSize - 1) / TileSize;
        _tiles = new byte[_columns * _rows];
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsEmpty => _count == 0;
    public bool IsFull => _count == _tiles.Length;

    /// <summary>Changed pixels (whole tiles, clipped to the picture) as a fraction of the picture.</summary>
    public double Coverage
    {
        get
        {
            Build();
            return (double)_area / ((long)Width * Height);
        }
    }

    /// <summary>Marks the tiles a rectangle touches; the part outside the picture is ignored.</summary>
    public void Add(int x, int y, int width, int height)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = (int)Math.Min(Width, (long)x + width), y1 = (int)Math.Min(Height, (long)y + height);
        if (x1 <= x0 || y1 <= y0) return;
        int c0 = x0 / TileSize, c1 = (x1 - 1) / TileSize, r0 = y0 / TileSize, r1 = (y1 - 1) / TileSize;
        for (int r = r0; r <= r1; r++)
        {
            int row = r * _columns;
            for (int c = c0; c <= c1; c++)
            {
                if (_tiles[row + c] != 0) continue;
                _tiles[row + c] = 1;
                _count++;
                _rectsValid = false;
            }
        }
    }

    public void Add(DirtyRect rect) => Add(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Adds everything another region holds; the two may differ in size (the part outside this picture is ignored).</summary>
    public void Add(DirtyRegion other)
    {
        if (ReferenceEquals(other, this) || other.IsEmpty) return;
        if (other.Width == Width && other.Height == Height)
        {
            for (int i = 0; i < _tiles.Length; i++)
            {
                if (other._tiles[i] == 0 || _tiles[i] != 0) continue;
                _tiles[i] = 1;
                _count++;
                _rectsValid = false;
            }
            return;
        }
        foreach (var r in other.GetRects()) Add(r);
    }

    public void AddAll()
    {
        if (IsFull) return;
        Array.Fill(_tiles, (byte)1);
        _count = _tiles.Length;
        _rectsValid = false;
    }

    public void Clear()
    {
        if (_count == 0) return;
        Array.Clear(_tiles);
        _count = 0;
        _rectsValid = false;
    }

    /// <summary>The changed tiles as non-overlapping rectangles clipped to the picture. Valid until the region changes.</summary>
    public IReadOnlyList<DirtyRect> GetRects()
    {
        Build();
        return _rects;
    }

    private void Build()
    {
        if (_rectsValid) return;
        _rects.Clear();
        _area = 0;
        // Runs of the previous tile row that can still grow downwards: (first column, end column, index in _rects).
        Span<(int C0, int C1, int Index)> open = _columns <= 256 ? stackalloc (int, int, int)[(_columns + 1) / 2] : new (int, int, int)[(_columns + 1) / 2];
        Span<(int C0, int C1, int Index)> next = _columns <= 256 ? stackalloc (int, int, int)[(_columns + 1) / 2] : new (int, int, int)[(_columns + 1) / 2];
        int openCount = 0;
        for (int r = 0; r < _rows && _count > 0; r++)
        {
            int nextCount = 0, o = 0, row = r * _columns;
            int y = r * TileSize, h = Math.Min(TileSize, Height - y);
            for (int c = 0; c < _columns;)
            {
                if (_tiles[row + c] == 0) { c++; continue; }
                int c0 = c;
                while (c < _columns && _tiles[row + c] != 0) c++;
                while (o < openCount && open[o].C0 < c0) o++;
                int index;
                if (o < openCount && open[o].C0 == c0 && open[o].C1 == c)
                {
                    index = open[o].Index;
                    var grown = _rects[index];
                    _rects[index] = grown with { Height = grown.Height + h };
                }
                else
                {
                    int x = c0 * TileSize;
                    index = _rects.Count;
                    _rects.Add(new DirtyRect(x, y, Math.Min(c * TileSize, Width) - x, h));
                }
                next[nextCount++] = (c0, c, index);
            }
            next[..nextCount].CopyTo(open);
            openCount = nextCount;
        }
        foreach (var rect in _rects) _area += rect.Area;
        _rectsValid = true;
    }
}
