using Avalonia;
using Avalonia.Controls;
using BetterVnc.Media;
using BetterVnc.Protocol;
using PixelRect = Avalonia.PixelRect;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// Puts the remote picture on the screen for <see cref="VncView"/>: the RFB framebuffer, the agent's stream, or the
/// stream over the part of the framebuffer it shows. One implementation per way of drawing, tried in this order:
/// <see cref="SwapChainPresenter"/> (Direct3D 11 in a native child window, the GPU path), <see cref="GlPresenter"/>
/// (OpenGL inside Avalonia's compositor), <see cref="BitmapPresenter"/> (a bitmap Avalonia draws, the last resort).
/// </summary>
internal interface IPicturePresenter : IDisposable
{
    /// <summary>The control <see cref="VncView"/> places over the visible part of the picture, never larger.</summary>
    Control Control { get; }

    /// <summary>
    /// True when <see cref="Control"/> is a native window: nothing Avalonia draws shows over it, so it steps aside while
    /// an overlay covers the picture.
    /// </summary>
    bool IsNativeWindow { get; }

    /// <summary>Short name for the status bar ("→ OpenGL").</summary>
    string PathName { get; }

    /// <summary>How the picture reaches the screen, for the status bar's tooltip and the stream messages.</summary>
    string Description { get; }

    /// <summary>The Direct3D device a hardware H.264 decoder should decode on; null when only CPU frames can be shown.</summary>
    D3D11VideoDevice? VideoDevice { get; }

    /// <summary>The presenter gave up for good (device lost, OpenGL unavailable), with the reason. Any thread.</summary>
    event Action<string>? Failed;

    /// <summary>The framebuffer to show; null for none (a session without VNC). UI thread.</summary>
    void SetFramebuffer(Framebuffer? framebuffer);

    /// <summary>These rectangles of the framebuffer changed; null: all of it (a new size). Any thread.</summary>
    void InvalidateFramebuffer(RfbRect[]? rects);

    /// <summary>
    /// Shows a decoded stream picture straight from the GPU or its NV12 planes, on the decode thread. False when this
    /// presenter has no GPU path or refused the picture; the caller then converts it and uses <see cref="PresentCpu"/>.
    /// </summary>
    bool PresentGpu(in DecodedFrame frame, (int X, int Y)? offset);

    /// <summary>The same for a BGRX picture (motion JPEG), on the decode thread.</summary>
    bool PresentGpu(ReadOnlySpan<byte> bgrx, int width, int height, (int X, int Y)? offset);

    /// <summary>
    /// Shows a BGRX stream picture from any thread; <paramref name="release"/> gets the buffer back once it has been
    /// copied. A picture that arrives before the last one was drawn replaces it.
    /// </summary>
    void PresentCpu(byte[] bgrx, int width, int height, Action<byte[]>? release, (int X, int Y)? offset);

    /// <summary>Takes the stream off the screen; the framebuffer shows again. Any thread.</summary>
    void HideStream();

    /// <summary>
    /// The size of <see cref="Control"/> in device pixels and where the whole picture lies in it (device pixels; it
    /// reaches outside the control where the picture is scrolled away, which crops it). UI thread, after layout.
    /// </summary>
    void SetLayout(PixelSize size, PixelRect destination);

    /// <summary>What shows, at its own size, as BGRA (alpha 255); null when nothing does. Any thread.</summary>
    byte[]? Capture(out int width, out int height);
}

/// <summary>
/// What the CPU presenters (<see cref="GlPresenter"/>, <see cref="BitmapPresenter"/>) draw from: the framebuffer and
/// its changed rectangles, and a copy of the latest stream picture. Thread-safe; the presenter takes the changes on the
/// UI thread when it draws.
/// </summary>
internal sealed class CpuPictureSource
{
    private readonly object _lock = new();
    private readonly List<RfbRect> _dirty = new();
    private bool _allDirty;
    private Framebuffer? _framebuffer;
    private byte[]? _stream;
    private int _streamWidth, _streamHeight;
    private (int X, int Y)? _streamOffset;
    private bool _streamShown, _streamChanged;

    /// <summary>Raised (any thread) when something changed that should be drawn.</summary>
    public event Action? Changed;

    public Framebuffer? Framebuffer { get { lock (_lock) return _framebuffer; } }

    public void SetFramebuffer(Framebuffer? framebuffer)
    {
        lock (_lock)
        {
            _framebuffer = framebuffer;
            _dirty.Clear();
            _allDirty = true;
        }
        Changed?.Invoke();
    }

    public void Invalidate(RfbRect[]? rects)
    {
        lock (_lock)
        {
            if (rects == null) _allDirty = true;
            else if (!_allDirty) _dirty.AddRange(rects);
        }
        Changed?.Invoke();
    }

    /// <summary>Copies the picture (newest wins) and gives the buffer back.</summary>
    public void SetStream(byte[] bgrx, int width, int height, Action<byte[]>? release, (int X, int Y)? offset)
    {
        try
        {
            if (width <= 0 || height <= 0 || bgrx.Length < width * height * 4) return;
            lock (_lock)
            {
                if (_stream == null || _stream.Length != width * height * 4) _stream = new byte[width * height * 4];
                Buffer.BlockCopy(bgrx, 0, _stream, 0, width * height * 4);
                _streamWidth = width;
                _streamHeight = height;
                _streamOffset = offset;
                _streamShown = _streamChanged = true;
            }
        }
        finally { release?.Invoke(bgrx); }
        Changed?.Invoke();
    }

    public void HideStream()
    {
        lock (_lock)
        {
            if (!_streamShown) return;
            _streamShown = false;
            _streamChanged = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Everything must be drawn again (the presenter lost what it had uploaded).</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _allDirty = true;
            _streamChanged = true;
        }
        Changed?.Invoke();
    }

    /// <summary>The framebuffer changes since the last call: all of it (<paramref name="all"/>) or these rectangles.</summary>
    public RfbRect[] TakeDirty(out bool all)
    {
        lock (_lock)
        {
            all = _allDirty;
            var rects = _dirty.ToArray();
            _dirty.Clear();
            _allDirty = false;
            return rects;
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> with the latest stream picture when it changed since the last call (pixels, size,
    /// offset; null pixels when the stream was hidden). False when nothing changed.
    /// </summary>
    public bool TakeStream(StreamUse use)
    {
        lock (_lock)
        {
            if (!_streamChanged) return false;
            _streamChanged = false;
            use(_streamShown ? _stream : null, _streamWidth, _streamHeight, _streamOffset);
            return true;
        }
    }

    public delegate void StreamUse(byte[]? bgrx, int width, int height, (int X, int Y)? offset);

    /// <summary>True while a stream picture shows.</summary>
    public bool StreamShown { get { lock (_lock) return _streamShown; } }

    /// <summary>
    /// What shows, composed on the CPU at its own size: the stream alone while a whole-desktop stream shows, otherwise
    /// the framebuffer with a partial stream pasted over its part. BGRA with alpha 255; null when there is nothing.
    /// </summary>
    public byte[]? Capture(out int width, out int height)
    {
        width = height = 0;
        lock (_lock)
        {
            var fb = _framebuffer;
            if (_streamShown && _stream != null && (_streamOffset == null || fb == null))
            {
                width = _streamWidth;
                height = _streamHeight;
                var copy = (byte[])_stream.Clone();
                Opaque(copy);
                return copy;
            }
            if (fb == null) return null;
            byte[] all;
            int fw, fh;
            lock (fb.SyncRoot) { all = fb.CopyOut(fb.Bounds); fw = fb.Width; fh = fb.Height; }
            if (fw <= 0 || fh <= 0) return null;
            if (_streamShown && _stream != null && _streamOffset is { } at)
            {
                var visible = new RfbRect(at.X, at.Y, _streamWidth, _streamHeight).Intersect(new RfbRect(0, 0, fw, fh));
                for (int row = 0; row < visible.Height; row++)
                    Buffer.BlockCopy(_stream, ((visible.Y - at.Y + row) * _streamWidth + visible.X - at.X) * 4, all, ((visible.Y + row) * fw + visible.X) * 4, visible.Width * 4);
            }
            Opaque(all);
            width = fw;
            height = fh;
            return all;
        }
    }

    private static void Opaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }
}
