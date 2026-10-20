using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BetterVnc.Media;
using BetterVnc.Protocol;
using PixelRect = Avalonia.PixelRect;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// The last resort, where neither the swap chain nor OpenGL is available: Avalonia bitmaps drawn like any control.
/// Only the changed rectangles are copied, but Avalonia uploads the whole bitmap on every change, so large pictures
/// lose frames (docs/AVALONIA-SPIKE.md, question 1).
/// </summary>
internal sealed class BitmapPresenter : Control, IPicturePresenter
{
    private readonly CpuPictureSource _source = new();
    private readonly Action _frameShown;
    private int _updateQueued;
    private WriteableBitmap? _framebuffer, _stream;
    private (int X, int Y)? _streamOffset;
    private PixelRect _destination;

    /// <param name="frameShown">Called for every picture that reached the screen.</param>
    public BitmapPresenter(Action frameShown)
    {
        _frameShown = frameShown;
        IsHitTestVisible = false;
        Focusable = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        _source.Changed += QueueUpdate;
    }

    public Control Control => this;
    public bool IsNativeWindow => false;
    public string PathName => "Avalonia bitmap";
    public string Description => "Avalonia bitmap (no GPU presentation and no OpenGL here)";
    public D3D11VideoDevice? VideoDevice => null;
#pragma warning disable CS0067 // nothing to lose: it draws as long as Avalonia does
    public event Action<string>? Failed;
#pragma warning restore CS0067

    public void SetFramebuffer(Framebuffer? framebuffer) => _source.SetFramebuffer(framebuffer);
    public void InvalidateFramebuffer(RfbRect[]? rects) => _source.Invalidate(rects);
    public bool PresentGpu(in DecodedFrame frame, (int X, int Y)? offset) => false;
    public bool PresentGpu(ReadOnlySpan<byte> bgrx, int width, int height, (int X, int Y)? offset) => false;
    public void PresentCpu(byte[] bgrx, int width, int height, Action<byte[]>? release, (int X, int Y)? offset) =>
        _source.SetStream(bgrx, width, height, release, offset);
    public void HideStream() => _source.HideStream();
    public byte[]? Capture(out int width, out int height) => _source.Capture(out width, out height);

    public void SetLayout(PixelSize size, PixelRect destination)
    {
        if (destination == _destination) return;
        _destination = destination;
        InvalidateVisual();
    }

    private void QueueUpdate()
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(Update, DispatcherPriority.Render);
    }

    private unsafe void Update()
    {
        Volatile.Write(ref _updateQueued, 0);
        bool changed = false;
        var rects = _source.TakeDirty(out bool all);
        var fb = _source.Framebuffer;
        if (fb == null)
        {
            changed = _framebuffer != null;
            _framebuffer?.Dispose();
            _framebuffer = null;
        }
        else if (all || rects.Length > 0)
        {
            lock (fb.SyncRoot)
            {
                int w = fb.Width, h = fb.Height;
                if (w > 0 && h > 0)
                {
                    if (_framebuffer == null || _framebuffer.PixelSize != new PixelSize(w, h))
                    {
                        _framebuffer?.Dispose();
                        _framebuffer = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                        all = true;
                    }
                    using var locked = _framebuffer.Lock();
                    fixed (byte* src = fb.Pixels)
                    {
                        foreach (var r0 in all ? new[] { fb.Bounds } : rects)
                        {
                            var r = r0.Intersect(fb.Bounds);
                            for (int y = 0; y < r.Height; y++)
                                Buffer.MemoryCopy(src + fb.Offset(r.X, r.Y + y), (byte*)locked.Address + (long)(r.Y + y) * locked.RowBytes + r.X * 4, r.Width * 4, r.Width * 4);
                        }
                    }
                    changed = true;
                }
            }
        }
        changed |= _source.TakeStream((bgrx, w, h, offset) =>
        {
            if (bgrx == null) { _stream?.Dispose(); _stream = null; return; }
            if (_stream == null || _stream.PixelSize != new PixelSize(w, h))
            {
                _stream?.Dispose();
                _stream = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var locked = _stream.Lock();
            fixed (byte* src = bgrx)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * w * 4, (byte*)locked.Address + (long)y * locked.RowBytes, w * 4, w * 4);
            _streamOffset = offset;
        });
        if (!changed) return;
        InvalidateVisual();
        _frameShown();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        double s = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var dest = new Rect(_destination.X / s, _destination.Y / s, _destination.Width / s, _destination.Height / s);
        if (dest.Width <= 0 || dest.Height <= 0) return;
        var fb = _framebuffer;
        var stream = _stream;
        if (fb != null && (stream == null || _streamOffset != null))
            context.DrawImage(fb, new Rect(fb.Size), dest);
        if (stream == null) return;
        if (_streamOffset is { } at && fb != null)
        {
            double sx = dest.Width / fb.PixelSize.Width, sy = dest.Height / fb.PixelSize.Height;
            var part = new Rect(dest.X + at.X * sx, dest.Y + at.Y * sy, stream.PixelSize.Width * sx, stream.PixelSize.Height * sy);
            using (context.PushClip(dest.Intersect(new Rect(Bounds.Size))))
                context.DrawImage(stream, new Rect(stream.Size), part);
        }
        else context.DrawImage(stream, new Rect(stream.Size), dest);
    }

    public void Dispose()
    {
        _source.Changed -= QueueUpdate;
        _framebuffer?.Dispose();
        _stream?.Dispose();
    }
}
