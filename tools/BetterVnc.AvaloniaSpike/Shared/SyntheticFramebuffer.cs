using System.Diagnostics;

namespace BetterVnc.AvaloniaSpike;

internal readonly record struct DirtyRect(int X, int Y, int Width, int Height);

/// <summary>
/// A framebuffer whose content changes 60 times a second on its own thread, like VncClient's Framebuffer: either only
/// the marker (a clock's or a blinking cursor's worth of pixels) or the whole picture (scrolling text-like detail).
/// </summary>
internal sealed class SyntheticFramebuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Block { get; }
    public byte[] Pixels { get; }
    public object SyncRoot { get; } = new();
    private readonly byte[] _pattern; // twice the height, scrolled through for the "full" mode
    private Thread? _thread;
    private volatile bool _stop;

    public SyntheticFramebuffer(int width, int height)
    {
        Width = width; Height = height;
        Block = Marker.BlockFor(width);
        Pixels = new byte[width * height * 4];
        _pattern = new byte[width * height * 2 * 4];
        Parallel.For(0, height * 2, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool glyph = (y % 14) < 10 && (x % 9) < 7 && ((Hash(y / 14, x / 9) & 3) != 0) && ((Hash(y, x) & 1) == 0);
                byte b = glyph ? (byte)40 : (byte)(200 + y % height * 40 / height), g = glyph ? (byte)40 : (byte)(205 + x * 30 / width), r = glyph ? (byte)50 : (byte)210;
                _pattern[i] = b; _pattern[i + 1] = g; _pattern[i + 2] = r; _pattern[i + 3] = 255;
            }
        });
        Buffer.BlockCopy(_pattern, 0, Pixels, 0, Pixels.Length);
    }

    private static int Hash(int a, int b) => (int)((uint)(a * 73856093 ^ b * 19349663) >> 7);

    /// <summary>Starts changing the picture; <paramref name="queue"/> gets the changed rectangles and the frame number.</summary>
    public void Start(bool full, Action<List<DirtyRect>, int> queue)
    {
        _thread = new Thread(() =>
        {
            Run.NameThisThread("producer");
            var markerRect = new DirtyRect(0, 0, Marker.Width(Block), Block);
            var all = new DirtyRect(0, 0, Width, Height);
            long interval = Stopwatch.Frequency / 60, next = Stopwatch.GetTimestamp();
            for (int t = 0; !_stop; t++)
            {
                while (Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
                next += interval;
                lock (SyncRoot)
                {
                    if (full)
                    {
                        int offset = (t * 4) % Height;
                        Buffer.BlockCopy(_pattern, offset * Width * 4, Pixels, 0, Pixels.Length);
                    }
                    Marker.Draw(Pixels, Width * 4, Block, t & 0xFFFF);
                }
                queue(new List<DirtyRect> { full ? all : markerRect }, t);
            }
        }) { IsBackground = true, Name = "framebuffer producer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop() { _stop = true; _thread?.Join(1000); }
}
