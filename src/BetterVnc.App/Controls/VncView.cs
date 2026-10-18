using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.App.Input;
using BetterVnc.App.Models;
using BetterVnc.Media;
using BetterVnc.Protocol;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Controls;

/// <summary>
/// Something that can scroll the remote machine by pixels (the guest agent). <paramref name="dx"/>/<paramref name="dy"/>
/// are pixels, positive = right / up; <paramref name="wheelDx"/>/<paramref name="wheelDy"/> the same movement in Windows
/// wheel units (120 per notch). Returns false when it cannot take the event right now, in which case the caller falls back
/// to VNC wheel clicks.
/// </summary>
public interface IScrollInjector
{
    bool TrySendScroll(double dx, double dy, int x, int y, string phase, double wheelDx, double wheelDy);
}

/// <summary>
/// Renders a <see cref="VncClient"/> framebuffer and forwards pointer input. Keyboard input is
/// fed in by the owning window (which owns the Win32 message hook) through <see cref="SendKey"/>.
/// </summary>
public sealed class VncView : Grid
{
    private readonly Image _image = new() { SnapsToDevicePixels = true };
    private WriteableBitmap? _bitmap;
    private VncClient? _client;
    private readonly object _dirtyLock = new();
    private readonly List<RfbRect> _dirty = new();
    private bool _renderQueued;
    private byte _buttons;
    private ScaleMode _scaleMode = ScaleMode.Fit;
    private readonly KeyboardTranslator _translator = new();
    private readonly Dictionary<uint, TranslatedKey> _pressedKeys = new();
    private Cursor _remoteCursor = Cursors.Arrow;
    private bool _mouseInside;
    private int _frames;
    private DateTime _frameWindowStart = DateTime.UtcNow;

    public KeyboardTranslator Translator => _translator;
    public double Fps { get; private set; }
    public event Action? RenderedFrame;

    public VncView()
    {
        Background = Brushes.Black;
        Focusable = true;
        FocusVisualStyle = null;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
        Children.Add(_image);
        ApplyScaleMode();
        AllowDrop = true;
        _scrollEndTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ScrollGestureTimeoutMs) };
        _scrollEndTimer.Tick += (_, _) => EndScrollGesture();
    }

    public ScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            _scaleMode = value;
            ApplyScaleMode();
        }
    }

    private void ApplyScaleMode()
    {
        if (_scaleMode == ScaleMode.Fit)
        {
            _image.Stretch = Stretch.Uniform;
            _image.HorizontalAlignment = HorizontalAlignment.Center;
            _image.VerticalAlignment = VerticalAlignment.Center;
            _image.Width = double.NaN;
            _image.Height = double.NaN;
        }
        else
        {
            // Fill (not None): the element is sized to the framebuffer, so the RFB picture lands at exactly one framebuffer
            // pixel per device pixel, and a streamed frame of another size (HiDPI capture) is scaled onto the same area.
            _image.Stretch = Stretch.Fill;
            _image.HorizontalAlignment = HorizontalAlignment.Left;
            _image.VerticalAlignment = VerticalAlignment.Top;
            if (_bitmap != null)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _image.Width = _bitmap.PixelWidth / dpi.DpiScaleX;
                _image.Height = _bitmap.PixelHeight / dpi.DpiScaleY;
            }
        }
    }

    // ------------------------------------------------------------------ external picture (agent video stream)

    private WriteableBitmap? _externalBitmap;
    private volatile bool _externalShown;
    /// <summary>
    /// Where partial frames land in the framebuffer (see <see cref="PresentExternalFrame"/>); null while frames show the
    /// whole desktop and are stretched over the framebuffer area. Only touched on the dispatcher.
    /// </summary>
    private RfbRect? _externalRect;

    /// <summary>True while the displayed picture comes from <see cref="PresentExternalFrame"/> rather than the RFB framebuffer.</summary>
    public bool IsExternalShown => _externalShown;

    private readonly object _pendingLock = new();
    private (byte[] Pixels, int Width, int Height, (int X, int Y)? Offset, Action<byte[]>? Release)? _pendingExternal;

    /// <summary>
    /// Shows a decoded frame from somewhere other than RFB (the guest agent's video stream) in place of the framebuffer.
    /// The RFB framebuffer keeps being decoded underneath so <see cref="ShowFramebuffer"/> can switch back instantly.
    /// <paramref name="bgrx"/> holds width*height*4 bytes; it belongs to the view until <paramref name="release"/> is
    /// called with it (or forever when no release is given). A frame that arrives before the previous one was drawn
    /// replaces it: the newest picture wins. Without <paramref name="offset"/> the frame is the whole remote desktop and
    /// is scaled over the framebuffer area. With one, it shows only the part of the desktop whose top-left corner is at
    /// that framebuffer position (the primary display of a desktop too large to encode): it is painted there pixel for
    /// pixel, clipped to the framebuffer, and the RFB picture stays visible around it. Safe to call from any thread.
    /// </summary>
    public void PresentExternalFrame(byte[] bgrx, int width, int height, Action<byte[]>? release = null, (int X, int Y)? offset = null)
    {
        if (width <= 0 || height <= 0 || bgrx.Length < width * height * 4) { release?.Invoke(bgrx); return; }
        bool queue;
        lock (_pendingLock)
        {
            if (_pendingExternal is { } superseded) superseded.Release?.Invoke(superseded.Pixels);
            queue = _pendingExternal == null;
            _pendingExternal = (bgrx, width, height, offset, release);
        }
        if (!queue) return;
        Dispatcher.BeginInvoke(() =>
        {
            (byte[] Pixels, int Width, int Height, (int X, int Y)? Offset, Action<byte[]>? Release) frame;
            lock (_pendingLock)
            {
                if (_pendingExternal is not { } pending) return;
                frame = pending;
                _pendingExternal = null;
            }
            try
            {
                if (_client == null) return;
                if (_gpuShown)
                {
                    // The GPU path refused this frame: WPF takes over, and the surface must not cover it.
                    _gpuShown = false;
                    _externalShown = false;
                    UpdateVideoPlacement();
                }
                if (frame.Offset is { } at)
                {
                    if (!PresentPartialFrame(frame.Pixels, frame.Width, frame.Height, at)) return;
                }
                else
                {
                    _externalRect = null;
                    if (_externalBitmap == null || _externalBitmap.PixelWidth != frame.Width || _externalBitmap.PixelHeight != frame.Height)
                        _externalBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    _externalBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
                }
                if (!_externalShown)
                {
                    _externalShown = true;
                    _image.Source = _externalBitmap;
                }
                else if (frame.Offset != null && !ReferenceEquals(_image.Source, _externalBitmap))
                {
                    _image.Source = _externalBitmap; // the framebuffer was resized meanwhile and the bitmap with it
                }
                CountFrame();
            }
            finally { frame.Release?.Invoke(frame.Pixels); }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Writes a frame that covers only part of the desktop into a framebuffer-sized external bitmap at <paramref name="at"/>.
    /// The rest of that bitmap carries the RFB picture: copied in full when the bitmap is new, the stream resumes or the
    /// frame moves, and kept current by <see cref="Render"/> afterwards. False when there is no framebuffer to paint into.
    /// </summary>
    private bool PresentPartialFrame(byte[] pixels, int width, int height, (int X, int Y) at)
    {
        var fb = _client!.Framebuffer;
        int fbW = fb.Width, fbH = fb.Height;
        if (fbW <= 0 || fbH <= 0) return false;
        bool fresh = _externalBitmap == null || _externalBitmap.PixelWidth != fbW || _externalBitmap.PixelHeight != fbH;
        if (fresh) _externalBitmap = new WriteableBitmap(fbW, fbH, 96, 96, PixelFormats.Bgr32, null);
        var target = new RfbRect(at.X, at.Y, width, height);
        if (fresh || !_externalShown || _externalRect != target)
        {
            _externalRect = target;
            CopyFramebufferInto(_externalBitmap!, fb, new[] { fb.Bounds }, exclude: null);
        }
        var visible = target.Intersect(fb.Bounds);
        if (visible.IsEmpty) return true; // the display lies outside what the VNC server shares; nothing to show of it
        var source = new Int32Rect(visible.X - at.X, visible.Y - at.Y, visible.Width, visible.Height);
        _externalBitmap!.WritePixels(source, pixels, width * 4, visible.X, visible.Y);
        return true;
    }

    /// <summary>
    /// Copies <paramref name="rects"/> of the RFB framebuffer into <paramref name="bmp"/>, which has the framebuffer's size,
    /// leaving <paramref name="exclude"/> (the streamed part) alone.
    /// </summary>
    private static unsafe void CopyFramebufferInto(WriteableBitmap bmp, Framebuffer fb, IEnumerable<RfbRect> rects, RfbRect? exclude)
    {
        bmp.Lock();
        try
        {
            lock (fb.SyncRoot)
            {
                if (fb.Width != bmp.PixelWidth || fb.Height != bmp.PixelHeight) return; // resize pending
                byte* dst = (byte*)bmp.BackBuffer;
                int dstStride = bmp.BackBufferStride;
                fixed (byte* src = fb.Pixels)
                {
                    foreach (var r0 in rects)
                    {
                        foreach (var r in Outside(r0.Intersect(fb.Bounds), exclude))
                        {
                            int rowBytes = r.Width * 4;
                            for (int y = 0; y < r.Height; y++)
                                Buffer.MemoryCopy(src + fb.Offset(r.X, r.Y + y), dst + (r.Y + y) * dstStride + r.X * 4, rowBytes, rowBytes);
                            bmp.AddDirtyRect(new Int32Rect(r.X, r.Y, r.Width, r.Height));
                        }
                    }
                }
            }
        }
        finally { bmp.Unlock(); }
    }

    /// <summary>The parts of <paramref name="r"/> not covered by <paramref name="hole"/>: up to four rectangles, none empty.</summary>
    private static IEnumerable<RfbRect> Outside(RfbRect r, RfbRect? hole)
    {
        if (r.IsEmpty) yield break;
        var i = hole is { } h ? r.Intersect(h) : default;
        if (i.IsEmpty) { yield return r; yield break; }
        if (i.Y > r.Y) yield return new RfbRect(r.X, r.Y, r.Width, i.Y - r.Y);
        if (i.Bottom < r.Bottom) yield return new RfbRect(r.X, i.Bottom, r.Width, r.Bottom - i.Bottom);
        if (i.X > r.X) yield return new RfbRect(r.X, i.Y, i.X - r.X, i.Height);
        if (i.Right < r.Right) yield return new RfbRect(i.Right, i.Y, r.Right - i.Right, i.Height);
    }

    /// <summary>Returns to the RFB framebuffer picture (repainting all of it). Safe to call from any thread.</summary>
    public void ShowFramebuffer()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_externalShown) return;
            _externalShown = false;
            if (_gpuShown)
            {
                // The framebuffer is already the image underneath; taking the surface away uncovers it.
                _gpuShown = false;
                UpdateVideoPlacement();
            }
            _image.Source = _bitmap;
            if (_client != null) lock (_dirtyLock) _dirty.Add(_client.Framebuffer.Bounds);
            QueueRender();
        }, DispatcherPriority.Render);
    }

    // ------------------------------------------------------------------ GPU presentation (agent video stream)

    private VideoSurfaceHost? _surface;
    private D3D11VideoDevice? _gpuDevice;
    private VideoRenderer? _renderer;      // read on the decode thread
    private bool _gpuUnavailable;
    private readonly object _gpuLock = new();
    private (int Width, int Height, (int X, int Y)? Offset) _gpuFrame; // the last picture presented; under _gpuLock
    private bool _gpuUpdateQueued;         // under _gpuLock
    private int _gpuFramesUncounted;       // Interlocked
    private bool _gpuShown;                // dispatcher only, like the rest below
    private bool _videoSurfaceBlocked;
    private VideoPlacement _placement;

    /// <summary>How streamed frames reach the screen: the GPU surface and its adapter, or why WPF draws them.</summary>
    public string GpuVideoStatus { get; private set; } = "GPU presentation not set up";

    /// <summary>
    /// The device the stream's H.264 decoder should decode on (DXVA), so its pictures go to the GPU surface without
    /// leaving the GPU; null while frames are drawn through WPF. Any thread.
    /// </summary>
    public D3D11VideoDevice? GpuVideoDevice => Volatile.Read(ref _renderer) != null ? _gpuDevice : null;

    /// <summary>
    /// True while a WPF element must show over the picture (the connecting / disconnected overlay): the GPU surface is a
    /// child window and would cover it, so it is taken away until this goes back to false.
    /// </summary>
    public bool VideoSurfaceBlocked
    {
        get => _videoSurfaceBlocked;
        set
        {
            _videoSurfaceBlocked = value;
            UpdateVideoPlacement();
        }
    }

    /// <summary>
    /// Sets up GPU presentation for streamed frames: a Direct3D 11 device (<see cref="GpuVideoDevice"/>) and a flip-model
    /// swap chain in a child window over the picture. False when Direct3D 11 is unavailable or failed before;
    /// <see cref="PresentVideoFrame(in DecodedFrame, ValueTuple{int, int}?)"/> then refuses frames and
    /// <see cref="PresentExternalFrame"/> draws them through WPF. Dispatcher only; cheap after the first call.
    /// </summary>
    public bool EnableGpuVideo()
    {
        if (_renderer != null) return true;
        if (_gpuUnavailable) return false;
        VideoRenderer? renderer = null;
        try
        {
            _gpuDevice = D3D11VideoDevice.Create();
            renderer = new VideoRenderer(_gpuDevice);
            var surface = new VideoSurfaceHost(UpdateVideoPlacement);
            var r = renderer;
            surface.WindowChanged += hwnd =>
            {
                r.SetWindow(hwnd);
                _placement = default; // the renderer starts over with this window: push the layout again
                if (hwnd == IntPtr.Zero) ShutDownGpu("the window closed");
                else UpdateVideoPlacement();
            };
            _surface = surface;
            Children.Add(surface);
            Volatile.Write(ref _renderer, renderer);
            GpuVideoStatus = $"GPU presentation on {_gpuDevice.AdapterName} (flip-model swap chain, frame latency 1, GPU thread priority {_gpuDevice.GpuThreadPriority})";
            return true;
        }
        catch (Exception ex)
        {
            if (_surface != null) Children.Remove(_surface);
            _surface = null;
            renderer?.Dispose();
            _gpuDevice?.Dispose();
            _gpuDevice = null;
            _gpuUnavailable = true;
            GpuVideoStatus = $"GPU presentation unavailable ({ex.Message}); frames are drawn through WPF";
            return false;
        }
    }

    /// <summary>
    /// Shows a decoded picture through the GPU surface: a hardware decoder's texture is copied on the GPU, a software
    /// decoder's NV12 is uploaded, and the shader converts and scales it; it is presented at once, so the newest frame is
    /// what the compositor shows. <paramref name="offset"/> works as for <see cref="PresentExternalFrame"/>. Call from the
    /// decoder's frame callback (the decode thread). False when the GPU path is not set up or gave up; the caller then
    /// converts the frame itself and uses <see cref="PresentExternalFrame"/>.
    /// </summary>
    public bool PresentVideoFrame(in DecodedFrame frame, (int X, int Y)? offset = null)
    {
        var renderer = Volatile.Read(ref _renderer);
        if (renderer == null) return false;
        if (!renderer.Present(in frame))
        {
            OnRendererRefused(renderer);
            return false;
        }
        QueueGpuUpdate(frame.Width, frame.Height, offset);
        return true;
    }

    /// <summary>The same for a BGRX picture (width*4 bytes per row): a motion JPEG frame.</summary>
    public bool PresentVideoFrame(byte[] bgrx, int width, int height, (int X, int Y)? offset = null)
    {
        var renderer = Volatile.Read(ref _renderer);
        if (renderer == null) return false;
        if (!renderer.Present(bgrx, width, height))
        {
            OnRendererRefused(renderer);
            return false;
        }
        QueueGpuUpdate(width, height, offset);
        return true;
    }

    private void OnRendererRefused(VideoRenderer renderer)
    {
        if (renderer.IsUsable) return; // just that frame (a texture it cannot show); the caller falls back for it
        string reason = renderer.Failure ?? "it was shut down";
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_renderer, renderer)) ShutDownGpu(reason); });
    }

    /// <summary>Drops GPU presentation for good (device lost, window gone); later frames go through WPF.</summary>
    private void ShutDownGpu(string reason)
    {
        var renderer = Interlocked.Exchange(ref _renderer, null);
        _gpuUnavailable = true;
        GpuVideoStatus = $"GPU presentation stopped ({reason}); frames are drawn through WPF";
        if (_gpuShown)
        {
            _gpuShown = false;
            _externalShown = false;
        }
        _placement = default;
        _surface?.Place(default);
        renderer?.Dispose();
        _gpuDevice?.Dispose();
        _gpuDevice = null;
    }

    /// <summary>Decode thread: remember what was presented and let the dispatcher show the surface and place it (coalesced).</summary>
    private void QueueGpuUpdate(int width, int height, (int X, int Y)? offset)
    {
        Interlocked.Increment(ref _gpuFramesUncounted);
        lock (_gpuLock)
        {
            _gpuFrame = (width, height, offset);
            if (_gpuUpdateQueued) return;
            _gpuUpdateQueued = true;
        }
        Dispatcher.BeginInvoke(ApplyGpuFrame, DispatcherPriority.Render);
    }

    private void ApplyGpuFrame()
    {
        lock (_gpuLock) _gpuUpdateQueued = false;
        int frames = Interlocked.Exchange(ref _gpuFramesUncounted, 0);
        if (_client == null || _renderer == null) return;
        if (!_gpuShown)
        {
            // From the RFB picture (or the WPF stream path) to the GPU surface. The framebuffer stays the image underneath:
            // it shows around a partial frame, and the moment the surface is taken away again.
            _gpuShown = true;
            _externalShown = true;
            _externalRect = null;
            _image.Source = _bitmap;
        }
        UpdateVideoPlacement(); // the frame may have changed size or position
        CountFrame(frames);
    }

    /// <summary>Puts the surface over the streamed picture, or shrinks it to nothing while it must not show. Dispatcher only.</summary>
    private void UpdateVideoPlacement()
    {
        var surface = _surface;
        var renderer = _renderer;
        if (surface == null || renderer == null) return;
        var placement = _gpuShown && !_videoSurfaceBlocked ? ComputePlacement() : default;
        if (placement == _placement) return;
        _placement = placement;
        surface.Place(placement);
        var d = placement.Destination;
        renderer.SetLayout(placement.Width, placement.Height, d.X, d.Y, d.Width, d.Height);
    }

    private VideoPlacement ComputePlacement()
    {
        var client = _client;
        if (client == null || _bitmap == null || !IsVisible) return default;
        if (PresentationSource.FromVisual(this) is not HwndSource { RootVisual: Visual root, CompositionTarget: { } target }) return default;
        if (!_image.IsDescendantOf(root)) return default;
        var m = target.TransformToDevice;
        PixelRect Device(Rect r) => r.IsEmpty ? default : new PixelRect(r.X * m.M11, r.Y * m.M22, r.Width * m.M11, r.Height * m.M22);
        var image = Device(_image.TransformToAncestor(root).TransformBounds(new Rect(_image.RenderSize)));
        var clip = Device(VisibleArea(root));
        (int Width, int Height, (int X, int Y)? Offset) frame;
        lock (_gpuLock) frame = _gpuFrame;
        var fb = client.Framebuffer;
        return VideoLayout.Place(image, fb.Width, fb.Height, frame.Width, frame.Height, frame.Offset, clip);
    }

    /// <summary>The part of this view that can be seen, inside every scroll viewport and clipping panel above it, in root coordinates.</summary>
    private Rect VisibleArea(Visual root)
    {
        var area = TransformToAncestor(root).TransformBounds(new Rect(RenderSize));
        for (var v = VisualTreeHelper.GetParent(this); v != null && !ReferenceEquals(v, root); v = VisualTreeHelper.GetParent(v))
        {
            if (v is UIElement e && (e is ScrollContentPresenter || e.ClipToBounds))
                area.Intersect(e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)));
            if (area.IsEmpty) break;
        }
        return area;
    }

    /// <summary>Takes any streamed picture off the screen at once (the connection it came from is going). Dispatcher only.</summary>
    private void HideStream()
    {
        _externalShown = false;
        _externalRect = null;
        if (_gpuShown)
        {
            _gpuShown = false;
            UpdateVideoPlacement();
        }
    }

    private void CountFrame(int frames = 1)
    {
        _frames += frames;
        var now = DateTime.UtcNow;
        var elapsed = (now - _frameWindowStart).TotalSeconds;
        if (elapsed >= 1)
        {
            Fps = _frames / elapsed;
            _frames = 0;
            _frameWindowStart = now;
        }
        RenderedFrame?.Invoke();
    }

    public void Attach(VncClient client)
    {
        Detach();
        _client = client;
        client.FramebufferUpdated += OnFramebufferUpdated;
        client.FramebufferResized += OnFramebufferResized;
        client.CursorChanged += OnCursorChanged;
        CreateBitmap(client.Framebuffer.Width, client.Framebuffer.Height);
        lock (_dirtyLock) _dirty.Add(client.Framebuffer.Bounds);
        QueueRender();
    }

    public void Detach()
    {
        HideStream();
        if (_client == null) return;
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.FramebufferResized -= OnFramebufferResized;
        _client.CursorChanged -= OnCursorChanged;
        _client = null;
    }

    private void CreateBitmap(int w, int h)
    {
        if (w <= 0 || h <= 0) { _bitmap = null; _image.Source = null; return; }
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        ApplyScaleMode();
    }

    private void OnFramebufferResized(int w, int h)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CreateBitmap(w, h);
            lock (_dirtyLock) { _dirty.Clear(); _dirty.Add(new RfbRect(0, 0, w, h)); }
            QueueRender();
        });
    }

    private void OnFramebufferUpdated(RfbRect[] rects)
    {
        lock (_dirtyLock)
        {
            _dirty.AddRange(rects);
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
    }

    private void QueueRender()
    {
        lock (_dirtyLock)
        {
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.BeginInvoke(Render, DispatcherPriority.Render);
    }

    private unsafe void Render()
    {
        RfbRect[] rects;
        lock (_dirtyLock)
        {
            rects = _dirty.ToArray();
            _dirty.Clear();
            _renderQueued = false;
        }
        var client = _client;
        var bmp = _bitmap;
        if (client == null || bmp == null || rects.Length == 0) return;
        var fb = client.Framebuffer;
        if (fb.Width != bmp.PixelWidth || fb.Height != bmp.PixelHeight) return; // resize pending

        bmp.Lock();
        try
        {
            lock (fb.SyncRoot)
            {
                byte* dst = (byte*)bmp.BackBuffer;
                int dstStride = bmp.BackBufferStride;
                fixed (byte* src = fb.Pixels)
                {
                    foreach (var r0 in rects)
                    {
                        var r = r0.Intersect(fb.Bounds);
                        if (r.IsEmpty) continue;
                        int rowBytes = r.Width * 4;
                        for (int y = 0; y < r.Height; y++)
                        {
                            Buffer.MemoryCopy(src + fb.Offset(r.X, r.Y + y), dst + (r.Y + y) * dstStride + r.X * 4, rowBytes, rowBytes);
                        }
                        bmp.AddDirtyRect(new Int32Rect(r.X, r.Y, r.Width, r.Height));
                    }
                }
            }
        }
        finally
        {
            bmp.Unlock();
        }

        // A stream of part of the desktop shows the RFB picture around it: keep that part of its bitmap current as well.
        if (_externalShown && _externalRect is { } streamed && _externalBitmap is { } ext)
            CopyFramebufferInto(ext, fb, rects, exclude: streamed);

        // While a stream is on screen the framebuffer is kept current but not shown, so it is not a rendered frame.
        if (!_externalShown) CountFrame();
    }

    private void OnCursorChanged(CursorShape shape)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _remoteCursor = CursorFactory.Create(shape, dpi.DpiScaleX);
            if (_mouseInside) Cursor = _remoteCursor;
        });
    }

    // ------------------------------------------------------------------ pointer

    private bool TryMapToFramebuffer(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_bitmap == null) return false;
        var local = TranslatePoint(p, _image);
        double sx = _bitmap.PixelWidth / Math.Max(1, _image.ActualWidth);
        double sy = _bitmap.PixelHeight / Math.Max(1, _image.ActualHeight);
        x = (int)Math.Round(local.X * sx);
        y = (int)Math.Round(local.Y * sy);
        return true;
    }

    private void SendPointer(Point p)
    {
        if (_client == null || !TryMapToFramebuffer(p, out var x, out var y)) return;
        _client.SendPointerEvent(x, y, _buttons);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _mouseInside = true;
        Cursor = _remoteCursor;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        Cursor = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SendPointer(e.GetPosition(this));
    }

    private static byte MaskFor(MouseButton b) => b switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Middle => PointerButton.Middle,
        MouseButton.Right => PointerButton.Right,
        MouseButton.XButton1 => PointerButton.Back,
        _ => 0,
    };

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Keyboard.Focus(this);
        _buttons |= MaskFor(e.ChangedButton);
        CaptureMouse();
        SendPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _buttons &= (byte)~MaskFor(e.ChangedButton);
        SendPointer(e.GetPosition(this));
        if (_buttons == 0) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SendWheel(e.Delta, horizontal: false, e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>VNC wheel clicks sent per wheel notch; macOS scrolls only a little per click, so 2-3 feels more natural there.</summary>
    public int ScrollClicksPerNotch { get; set; } = 1;

    private int _wheelAccumulatorV;
    private int _wheelAccumulatorH;
    private readonly Queue<long> _wheelHistory = new();

    /// <summary>
    /// High-resolution scroll sink (the guest agent). While it accepts events the wheel is forwarded as pixel deltas and no
    /// VNC wheel clicks are sent; when it is null or declines, the VNC path below takes over.
    /// </summary>
    public IScrollInjector? ScrollInjector { get; set; }

    /// <summary>Pixels one "line" of wheel scrolling moves, matching what Windows itself uses for a notch at 3 lines (48 px).</summary>
    public const double PixelsPerLine = 16;
    /// <summary>A gesture ends after this long without a wheel event (the agent then gets a zero-delta "end").</summary>
    public const int ScrollGestureTimeoutMs = 150;

    private readonly DispatcherTimer _scrollEndTimer;
    private bool _scrollGestureActive;
    private int _scrollGestureX, _scrollGestureY;

    public void SendWheel(int delta, bool horizontal, Point position)
    {
        if (_client == null || !TryMapToFramebuffer(position, out var x, out var y)) return;
        if (TrySendSmoothScroll(delta, horizontal, x, y)) return;
        // Precision touchpads deliver many small deltas; accumulate to whole notches (WHEEL_DELTA = 120)
        // so one physical notch always becomes exactly one (or ScrollClicksPerNotch) VNC wheel click.
        ref int acc = ref (horizontal ? ref _wheelAccumulatorH : ref _wheelAccumulatorV);
        acc += delta;
        int notches = acc / 120;
        if (notches == 0) return;
        acc -= notches * 120;
        byte button = horizontal
            ? (notches > 0 ? PointerButton.WheelRight : PointerButton.WheelLeft)
            : (notches > 0 ? PointerButton.WheelUp : PointerButton.WheelDown);
        // Velocity acceleration: a quick flick (several notches within 250 ms) sends up to 4x the
        // clicks, a slow deliberate turn stays precise. Mirrors how macOS treats its own wheel.
        var now = Environment.TickCount64;
        _wheelHistory.Enqueue(now);
        while (_wheelHistory.Count > 0 && now - _wheelHistory.Peek() > 250) _wheelHistory.Dequeue();
        int boost = Math.Clamp(_wheelHistory.Count, 1, 4);
        int clicks = Math.Abs(notches) * Math.Max(1, ScrollClicksPerNotch) * boost;
        for (int i = 0; i < clicks; i++)
        {
            _client.SendPointerEvent(x, y, (byte)(_buttons | button));
            _client.SendPointerEvent(x, y, _buttons);
        }
    }

    /// <summary>
    /// Sends the wheel movement to the guest agent as pixels: WHEEL_DELTA (120) is one notch, the Windows "lines per notch"
    /// setting says how far a notch scrolls locally, and a line is <see cref="PixelsPerLine"/>. The raw delta travels too,
    /// so a Windows guest can replay it exactly. Returns false when no agent takes scroll events.
    /// </summary>
    private bool TrySendSmoothScroll(int delta, bool horizontal, int x, int y)
    {
        var injector = ScrollInjector;
        if (injector == null) return false;
        double pixels = delta / 120.0 * (horizontal ? WheelScrollChars : WheelScrollLines) * PixelsPerLine;
        double dx = horizontal ? pixels : 0, dy = horizontal ? 0 : pixels;
        double wdx = horizontal ? delta : 0, wdy = horizontal ? 0 : delta;
        var phase = _scrollGestureActive ? AgentScrollPhase.Continue : AgentScrollPhase.Begin;
        if (!injector.TrySendScroll(dx, dy, x, y, phase, wdx, wdy)) return false;
        _scrollGestureActive = true;
        _scrollGestureX = x;
        _scrollGestureY = y;
        _scrollEndTimer.Stop();
        _scrollEndTimer.Start();
        _wheelAccumulatorV = _wheelAccumulatorH = 0; // the VNC path starts clean if the agent drops out
        return true;
    }

    private void EndScrollGesture()
    {
        _scrollEndTimer.Stop();
        if (!_scrollGestureActive) return;
        _scrollGestureActive = false;
        ScrollInjector?.TrySendScroll(0, 0, _scrollGestureX, _scrollGestureY, AgentScrollPhase.End, 0, 0);
    }

    /// <summary>Windows "wheel scroll lines" setting; -1 means a page, approximated as 10 lines.</summary>
    private static double WheelScrollLines
    {
        get
        {
            int lines = SystemParameters.WheelScrollLines;
            return lines switch { < 0 => 10, 0 => 0, _ => lines };
        }
    }

    /// <summary>Windows "wheel scroll characters" setting for a tilted wheel (default 3).</summary>
    private static double WheelScrollChars =>
        SystemParametersInfo(SPI_GETWHEELSCROLLCHARS, 0, out uint chars, 0) && chars > 0 && chars < 1000 ? chars : 3;

    // ------------------------------------------------------------------ keyboard

    /// <summary>Called by the owning window for every raw key message while this view has focus.</summary>
    public void SendKey(int vk, int scancode, bool extended, bool down)
    {
        if (_client == null) return;
        var key = _translator.Translate(vk, scancode, extended);
        uint id = key.Scancode != 0 ? key.Scancode : key.Keysym | 0x80000000;
        if (down)
        {
            _pressedKeys[id] = key;
        }
        else
        {
            // Release with the keysym we pressed with, so Shift changes mid-press do not strand a key.
            if (_pressedKeys.Remove(id, out var pressed)) key = pressed;
        }
        _client.SendKeyEvent(key.Keysym, down, key.Scancode);
    }

    public void SendKeysym(uint keysym, bool down) => _client?.SendKeyEvent(keysym, down, 0);

    public void SendCtrlAltDel()
    {
        if (_client == null) return;
        _client.SendKeyEvent(Keysym.ControlL, true, 0x1D);
        _client.SendKeyEvent(Keysym.AltL, true, 0x38);
        _client.SendKeyEvent(Keysym.Delete, true, 0xE053);
        _client.SendKeyEvent(Keysym.Delete, false, 0xE053);
        _client.SendKeyEvent(Keysym.AltL, false, 0x38);
        _client.SendKeyEvent(Keysym.ControlL, false, 0x1D);
    }

    /// <summary>Releases every key the remote believes is down (focus loss, disconnect).</summary>
    public void ReleaseAllKeys()
    {
        if (_client == null) { _pressedKeys.Clear(); return; }
        foreach (var k in _pressedKeys.Values)
            _client.SendKeyEvent(k.Keysym, false, k.Scancode);
        _pressedKeys.Clear();
        if (_buttons != 0)
        {
            _buttons = 0;
            if (_mouseInside) SendPointer(Mouse.GetPosition(this));
        }
    }

    /// <summary>Copies the picture on screen (the streamed frame while one is shown, else the framebuffer) to a bitmap for screenshots.</summary>
    public BitmapSource? Snapshot()
    {
        var client = _client;
        if (client == null) return null;
        if (_gpuShown && _renderer?.CaptureFrame(out int w, out int h) is { } frame)
        {
            (int Width, int Height, (int X, int Y)? Offset) shown;
            lock (_gpuLock) shown = _gpuFrame;
            if (shown.Offset is not { } at) return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, frame, w * 4);
            // A partial frame: paste the streamed display into the framebuffer picture, as it is on screen.
            var desk = client.Framebuffer;
            byte[] all;
            int fw, fh;
            lock (desk.SyncRoot) { all = desk.CopyOut(desk.Bounds); fw = desk.Width; fh = desk.Height; }
            var visible = new RfbRect(at.X, at.Y, w, h).Intersect(new RfbRect(0, 0, fw, fh));
            for (int row = 0; row < visible.Height; row++)
                Buffer.BlockCopy(frame, ((visible.Y - at.Y + row) * w + visible.X - at.X) * 4, all, ((visible.Y + row) * fw + visible.X) * 4, visible.Width * 4);
            return BitmapSource.Create(fw, fh, 96, 96, PixelFormats.Bgr32, null, all, fw * 4);
        }
        if (_externalShown && _externalBitmap != null)
        {
            var copy = _externalBitmap.Clone();
            copy.Freeze();
            return copy;
        }
        var fb = client.Framebuffer;
        byte[] pixels;
        lock (fb.SyncRoot) pixels = fb.CopyOut(fb.Bounds);
        return BitmapSource.Create(fb.Width, fb.Height, 96, 96, PixelFormats.Bgra32, null, pixels, fb.Width * 4);
    }
}
