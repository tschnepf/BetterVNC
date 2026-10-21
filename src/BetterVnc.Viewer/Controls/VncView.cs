using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.Media;
using BetterVnc.Protocol;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Input;
using static BetterVnc.Viewer.Input.NativeMethods;
using PixelRect = Avalonia.PixelRect;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace BetterVnc.Viewer.Controls;

/// <summary>A copy of the picture on screen: BGRA, alpha 255, <see cref="Width"/>*4 bytes per row.</summary>
public sealed record PictureSnapshot(byte[] Bgra, int Width, int Height);

/// <summary>
/// Shows a <see cref="VncClient"/>'s framebuffer and forwards pointer input. Keyboard input is fed in by the owning
/// window (which owns the window-procedure hook) through <see cref="SendKey"/>. The agent's stream is drawn here too,
/// as the session's <see cref="IVideoSurface"/>. A session without VNC attaches with <see cref="AttachAgent"/>
/// instead: the picture then comes only from the agent's stream, and pointer and keys go to the agent.
/// <para>
/// The whole picture, framebuffer and stream alike, goes through one presenter (<see cref="IPicturePresenter"/>): the
/// GPU swap chain in a native child window where it can, OpenGL or a bitmap otherwise. The presenter's control is kept
/// exactly the size of the visible part of the picture (a native window ignores Avalonia's clipping), and scrolling and
/// fit scaling go into where the picture lies inside it.
/// </para>
/// </summary>
public sealed class VncView : Panel, IVideoSurface
{
    private enum PresenterKind { SwapChain, OpenGl, Bitmap }

    private readonly Canvas _canvas = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Image _still = new() { IsVisible = false, Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal, IsVisible = false, AllowAutoHide = false, SmallChange = 40 };
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical, IsVisible = false, AllowAutoHide = false, SmallChange = 40 };
    private IPicturePresenter? _presenter;
    private PresenterKind _presenterKind;
    private string? _fallbackReason;

    private VncClient? _client;
    private (int Width, int Height)? _picture; // what the picture is, in its own pixels (framebuffer, or the agent's frames); UI thread
    private ScaleMode _scaleMode = ScaleMode.Fit;
    private int _scrollX, _scrollY;
    private bool _updatingBars;
    private double _scale = 1;
    private PixelRect _pictureRect;            // where the whole picture lies, device pixels relative to the canvas
    private PixelSize _presenterSize;          // the presenter control's size in device pixels; zero while hidden
    private PixelRect _presenterDestination;   // where the whole picture lies relative to the presenter control
    private bool _presenterLayoutQueued;
    private bool _videoSurfaceBlocked;
    private Bitmap? _stillBitmap;
    private TopLevel? _topLevel;

    private byte _buttons;
    private readonly KeyboardTranslator _translator = new();
    private readonly Dictionary<uint, TranslatedKey> _pressedKeys = new();
    private Cursor _remoteCursor = new(StandardCursorType.Arrow);
    private Cursor? _previousCursor;
    private readonly Cursor _noCursor = new(StandardCursorType.None);
    private bool _remoteCursorVisible = true;
    private bool _pointerInside;
    private Point _lastPointer;
    // Agent-only sessions (no VncClient): where input goes, and what the remote believes is held down.
    private IAgentInput? _agentInput;
    private int _agentButtons;
    private (int X, int Y) _agentPointer;
    private readonly Dictionary<int, (int Scan, int Vk)> _agentKeysDown = new();

    // Stream frames come from the decode thread; the gate keeps a frame of a session that is going from showing.
    private readonly object _streamGate = new();
    private bool _streamAllowed;               // under _streamGate
    private long _streamSize;                  // the last frame's width << 32 | height; Interlocked
    private int _streamSizeQueued;

    private int _framesPending, _framesQueued; // Interlocked
    private int _frames;
    private DateTime _frameWindowStart = DateTime.UtcNow;

    public KeyboardTranslator Translator => _translator;
    public double Fps { get; private set; }
    public event Action? RenderedFrame;

    public VncView()
    {
        Focusable = true;
        FocusAdorner = null;
        ClipToBounds = true;
        Background = Brushes.Black;
        _canvas.Children.Add(_still);
        Children.Add(_canvas);
        Children.Add(_hbar);
        Children.Add(_vbar);
        _hbar.ValueChanged += (_, e) => OnScrolled(horizontal: true, e.NewValue);
        _vbar.ValueChanged += (_, e) => OnScrolled(horizontal: false, e.NewValue);
        _canvas.PointerEntered += OnPointerEntered;
        _canvas.PointerExited += OnPointerExited;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _scrollEndTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ScrollGestureTimeoutMs) };
        _scrollEndTimer.Tick += (_, _) => EndScrollGesture();
        UsePresenter(Environment.GetEnvironmentVariable("BETTERVNC_PICTURE")?.Trim().ToLowerInvariant() switch
        {
            "gl" or "opengl" => PresenterKind.OpenGl,
            "bitmap" => PresenterKind.Bitmap,
            _ => PresenterKind.SwapChain,
        });
    }

    public ScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            if (_scaleMode == value) return;
            _scaleMode = value;
            InvalidateArrange();
            PictureAreaChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ presenter

    /// <summary>
    /// Sets up the way the picture reaches the screen, falling back down the list (swap chain, OpenGL, bitmap) while
    /// one cannot be created. Takes over the framebuffer from the previous presenter. UI thread.
    /// </summary>
    private void UsePresenter(PresenterKind kind)
    {
        var old = _presenter;
        _presenter = null;
        if (old != null)
        {
            Children.Remove(old.Control);
            old.Dispose();
        }
        while (true)
        {
            try
            {
                IPicturePresenter presenter = kind switch
                {
                    PresenterKind.SwapChain => new SwapChainPresenter(FrameShown),
                    PresenterKind.OpenGl => new GlPresenter(FrameShown),
                    _ => new BitmapPresenter(FrameShown),
                };
                presenter.Failed += reason => Dispatcher.UIThread.Post(() => OnPresenterFailed(presenter, reason));
                _presenter = presenter;
                _presenterKind = kind;
                break;
            }
            catch (Exception ex) when (kind != PresenterKind.Bitmap)
            {
                NoteFallback(kind, ex.Message);
                kind = kind + 1;
            }
        }
        // Above the canvas, below the scroll bars (they never overlap).
        Children.Insert(1, _presenter.Control);
        _presenterSize = default;
        _presenter.SetFramebuffer(_client?.Framebuffer);
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnPresenterFailed(IPicturePresenter presenter, string reason)
    {
        if (!ReferenceEquals(presenter, _presenter) || _presenterKind == PresenterKind.Bitmap) return;
        NoteFallback(_presenterKind, reason);
        UsePresenter(_presenterKind + 1);
    }

    private void NoteFallback(PresenterKind kind, string reason)
    {
        string what = kind == PresenterKind.SwapChain ? "GPU presentation" : "OpenGL";
        _fallbackReason = $"{what} unavailable ({reason})";
        System.Diagnostics.Trace.WriteLine("BetterVNC picture: " + _fallbackReason);
    }

    private void ShutDownPresenter()
    {
        var presenter = _presenter;
        if (presenter == null) return;
        lock (_streamGate) _streamAllowed = false;
        _presenter = null;
        Children.Remove(presenter.Control);
        presenter.Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.ScalingChanged += OnScalingChanged;
        if (_presenter == null) UsePresenter(_presenterKind);
        lock (_streamGate) _streamAllowed = IsAttached;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        ShutDownPresenter();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        InvalidateArrange();
        PictureAreaChanged?.Invoke();
    }

    // ------------------------------------------------------------------ IVideoSurface: the agent's stream

    /// <summary>How the picture reaches the screen: the GPU surface and its adapter, or what draws it instead and why.</summary>
    public string GpuVideoStatus => _presenter is not { } p ? "no picture surface (the window is closed)"
        : _fallbackReason is { } why ? $"{p.Description}; {why}" : p.Description;

    /// <summary>The device the stream's H.264 decoder should decode on (DXVA); null while frames go through the CPU path. Any thread.</summary>
    public D3D11VideoDevice? GpuVideoDevice => Volatile.Read(ref _presenter)?.VideoDevice;

    public bool HasGpuVideo => GpuVideoDevice != null;

    public string CpuPathName => Volatile.Read(ref _presenter)?.PathName ?? "nothing";

    /// <summary>The picture surface is set up when the view is; true when it is the GPU one.</summary>
    public bool EnableGpuVideo() => HasGpuVideo;

    /// <summary>A Media Foundation decoder, with DXVA on <see cref="GpuVideoDevice"/> when <paramref name="hardware"/>. Any thread.</summary>
    public IH264Decoder CreateH264Decoder(bool hardware) => new H264Decoder(hardware ? GpuVideoDevice : null);

    /// <summary>
    /// Shows a decoded stream picture on the GPU path, from the decoder's frame callback (the decode thread). Without
    /// <paramref name="offset"/> the frame is the whole remote desktop and covers the framebuffer; with one it shows the
    /// part of the desktop whose top-left corner is at that framebuffer position and is drawn there, clipped to the
    /// framebuffer, with the RFB picture around it. False when there is no GPU path; the caller then converts the frame
    /// and uses <see cref="PresentExternalFrame"/>.
    /// </summary>
    public bool PresentVideoFrame(in DecodedFrame frame, (int X, int Y)? offset = null)
    {
        var presenter = Volatile.Read(ref _presenter);
        if (presenter == null) return false;
        lock (_streamGate)
        {
            if (!_streamAllowed) return true; // the session it belongs to has gone: drop it
            if (!presenter.PresentGpu(in frame, offset)) return false;
        }
        NoteStreamFrame(frame.Width, frame.Height);
        return true;
    }

    /// <summary>The same for a BGRX picture (width*4 bytes per row): a motion JPEG frame.</summary>
    public bool PresentVideoFrame(byte[] bgrx, int width, int height, (int X, int Y)? offset = null)
    {
        var presenter = Volatile.Read(ref _presenter);
        if (presenter == null) return false;
        lock (_streamGate)
        {
            if (!_streamAllowed) return true;
            if (!presenter.PresentGpu(bgrx, width, height, offset)) return false;
        }
        NoteStreamFrame(width, height);
        return true;
    }

    /// <summary>
    /// Shows a BGRX stream picture through the presenter's CPU path, from any thread; <paramref name="release"/> gets the
    /// buffer back once it has been copied. <paramref name="offset"/> works as for <see cref="PresentVideoFrame(in DecodedFrame, ValueTuple{int, int}?)"/>.
    /// </summary>
    public void PresentExternalFrame(byte[] bgrx, int width, int height, Action<byte[]>? release = null, (int X, int Y)? offset = null)
    {
        var presenter = Volatile.Read(ref _presenter);
        if (presenter == null || width <= 0 || height <= 0 || bgrx.Length < width * height * 4) { release?.Invoke(bgrx); return; }
        lock (_streamGate)
        {
            if (!_streamAllowed) { release?.Invoke(bgrx); return; }
            presenter.PresentCpu(bgrx, width, height, release, offset);
        }
        NoteStreamFrame(width, height);
    }

    /// <summary>Returns to the RFB framebuffer picture. Any thread.</summary>
    public void ShowFramebuffer() => Volatile.Read(ref _presenter)?.HideStream();

    /// <summary>Decode thread: a session without VNC takes its picture size from the frames (coalesced onto the UI thread).</summary>
    private void NoteStreamFrame(int width, int height)
    {
        Interlocked.Exchange(ref _streamSize, (long)width << 32 | (uint)height);
        if (_agentInput == null || Interlocked.Exchange(ref _streamSizeQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _streamSizeQueued, 0);
            long size = Interlocked.Read(ref _streamSize);
            EnsureAgentPicture((int)(size >> 32), (int)(size & 0xFFFFFFFF));
        }, DispatcherPriority.Render);
    }

    /// <summary>Any thread: a picture reached the screen. Counted on the UI thread for the frame rate.</summary>
    private void FrameShown()
    {
        Interlocked.Increment(ref _framesPending);
        if (Interlocked.Exchange(ref _framesQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _framesQueued, 0);
            CountFrame(Interlocked.Exchange(ref _framesPending, 0));
        }, DispatcherPriority.Render);
    }

    private void CountFrame(int frames)
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

    /// <summary>
    /// True while an Avalonia element must show over the picture (the connecting / disconnected overlay): a native
    /// picture surface would cover it, so it steps aside and a still of the last picture shows in its place.
    /// </summary>
    public bool VideoSurfaceBlocked
    {
        get => _videoSurfaceBlocked;
        set
        {
            if (_videoSurfaceBlocked == value) return;
            _videoSurfaceBlocked = value;
            if (value && _presenter is { IsNativeWindow: true }) ShowStill();
            else HideStill();
            InvalidateArrange();
        }
    }

    private void ShowStill()
    {
        HideStill();
        if (!IsAttached || Snapshot() is not { } shot) return;
        var handle = GCHandle.Alloc(shot.Bgra, GCHandleType.Pinned);
        try
        {
            _stillBitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(), new PixelSize(shot.Width, shot.Height), new Vector(96, 96), shot.Width * 4);
        }
        finally { handle.Free(); }
        _still.Source = _stillBitmap;
        _still.IsVisible = true;
    }

    private void HideStill()
    {
        _still.IsVisible = false;
        _still.Source = null;
        _stillBitmap?.Dispose();
        _stillBitmap = null;
    }

    // ------------------------------------------------------------------ sessions

    public void Attach(VncClient client)
    {
        Detach();
        _client = client;
        client.FramebufferUpdated += OnFramebufferUpdated;
        client.FramebufferResized += OnFramebufferResized;
        client.CursorChanged += OnCursorChanged;
        // The first shape usually arrives before this view subscribes.
        if (client.CurrentCursor is { } cursor) OnCursorChanged(cursor);
        var fb = client.Framebuffer;
        SetPicture(fb.Width > 0 && fb.Height > 0 ? (fb.Width, fb.Height) : null);
        _presenter?.HideStream();
        _presenter?.SetFramebuffer(fb);
        lock (_streamGate) _streamAllowed = _presenter != null;
    }

    /// <summary>True while the view shows a remote: attached to a VNC client, or to the guest agent alone.</summary>
    public bool IsAttached => _client != null || _agentInput != null;

    /// <summary>True for a session without VNC (<see cref="AttachAgent"/>).</summary>
    public bool IsAgentOnly => _agentInput != null;

    /// <summary>
    /// Shows a session that has no VNC connection: the picture comes only from streamed frames, each the whole picture
    /// whatever part of the desktop it shows, and pointer and keys go to <paramref name="input"/>. The pointer's shape
    /// arrives through <see cref="SetRemoteCursor"/>. Black, and no pointer events, until the stream's size is known
    /// (<see cref="SetAgentPictureSize"/> or the first frame).
    /// </summary>
    public void AttachAgent(IAgentInput input)
    {
        Detach();
        _agentInput = input;
        SetPicture(null);
        _presenter?.SetFramebuffer(null);
        _presenter?.HideStream();
        SetCursor(new Cursor(StandardCursorType.Arrow));
        _remoteCursorVisible = true;
        ApplyCursor();
        lock (_streamGate) _streamAllowed = _presenter != null;
    }

    /// <summary>Agent-only sessions: the stream's picture size, from its start, so layout and pointer are right before the first frame. UI thread.</summary>
    public void SetAgentPictureSize(int width, int height) => EnsureAgentPicture(width, height);

    private void EnsureAgentPicture(int width, int height)
    {
        if (_agentInput == null || width <= 0 || height <= 0) return;
        SetPicture((width, height));
    }

    private void SetPicture((int Width, int Height)? picture)
    {
        if (_picture == picture) return;
        _picture = picture;
        InvalidateArrange();
        PictureAreaChanged?.Invoke(); // at 1:1 the picture area is the picture
    }

    /// <summary>The pointer's shape from somewhere other than RFB (the guest agent, for sessions without VNC). Any thread.</summary>
    public void SetRemoteCursor(CursorShape shape) => OnCursorChanged(shape);

    /// <summary>Hides the pointer over the picture while the remote hides its own (typing, a game). Any thread.</summary>
    public void SetRemoteCursorVisible(bool visible)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _remoteCursorVisible = visible;
            ApplyCursor();
        });
    }

    public void Detach()
    {
        lock (_streamGate) _streamAllowed = false;
        // Any frame in flight has been shown by now (the gate); take the stream away. The framebuffer stays on screen,
        // frozen, until the next session replaces it.
        _presenter?.HideStream();
        _agentInput = null;
        _agentButtons = 0;
        _agentKeysDown.Clear();
        if (_client == null) return;
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.FramebufferResized -= OnFramebufferResized;
        _client.CursorChanged -= OnCursorChanged;
        _client = null;
    }

    private void OnFramebufferResized(int w, int h)
    {
        Volatile.Read(ref _presenter)?.InvalidateFramebuffer(null);
        Dispatcher.UIThread.Post(() => { if (_client != null) SetPicture(w > 0 && h > 0 ? (w, h) : null); });
    }

    private void OnFramebufferUpdated(RfbRect[] rects) => Volatile.Read(ref _presenter)?.InvalidateFramebuffer(rects);

    private void OnCursorChanged(CursorShape shape)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetCursor(CursorFactory.Create(shape));
            ApplyCursor();
        });
    }

    /// <summary>The previous remote cursor is disposed one change later, so the OS never holds a destroyed cursor.</summary>
    private void SetCursor(Cursor cursor)
    {
        _previousCursor?.Dispose();
        _previousCursor = _remoteCursor;
        _remoteCursor = cursor;
    }

    private void ApplyCursor() => _canvas.Cursor = _remoteCursorVisible ? _remoteCursor : _noCursor;

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// The area the picture is shown in, in device pixels: the whole view when the picture is fitted to the window, the
    /// picture's size at 1:1. A guest agent that can scale before encoding sends a stream no larger than this (keeping
    /// the desktop's shape), so the stream lands on screen pixel for pixel. Null while the view has no size or, at 1:1,
    /// no picture. UI thread.
    /// </summary>
    public (int Width, int Height)? PictureArea
    {
        get
        {
            if (_scaleMode == ScaleMode.Fit)
            {
                // The view, not the picture: a stream rescaled to the picture would change the picture again.
                double s = Scaling;
                int w = (int)Math.Round(Bounds.Width * s), h = (int)Math.Round(Bounds.Height * s);
                return w > 0 && h > 0 ? (w, h) : null;
            }
            return _picture;
        }
    }

    /// <summary>
    /// <see cref="PictureArea"/> may have changed: the view was resized or moved to a screen with another scaling, the
    /// scale mode changed, or the picture did. Raised on the UI thread, often several times in a row while a window is dragged.
    /// </summary>
    public event Action? PictureAreaChanged;

    private double Scaling => _topLevel?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PictureAreaChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _hbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _vbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _canvas.Measure(availableSize);
        _presenter?.Control.Measure(availableSize);
        double s = Scaling;
        double w = double.IsInfinity(availableSize.Width) ? (_picture?.Width ?? 0) / s : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? (_picture?.Height ?? 0) / s : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double s = _scale = Scaling;
        int Px(double dip) => (int)Math.Round(dip * s);
        int vw = Px(finalSize.Width), vh = Px(finalSize.Height);
        int barW = Math.Max(1, Px(_vbar.DesiredSize.Width > 0 ? _vbar.DesiredSize.Width : 12));
        int barH = Math.Max(1, Px(_hbar.DesiredSize.Height > 0 ? _hbar.DesiredSize.Height : 12));

        // Scroll bars only at 1:1 for a picture larger than the view; one bar can make the other necessary.
        bool hbar = false, vbar = false;
        if (_scaleMode == ScaleMode.OneToOne && _picture is { } pic)
            for (int i = 0; i < 2; i++)
            {
                hbar = pic.Width > vw - (vbar ? barW : 0);
                vbar = pic.Height > vh - (hbar ? barH : 0);
            }
        int viewW = Math.Max(0, vw - (vbar ? barW : 0)), viewH = Math.Max(0, vh - (hbar ? barH : 0));

        var picture = default(PixelRect);
        if (_picture is { } p && viewW > 0 && viewH > 0)
        {
            if (_scaleMode == ScaleMode.Fit)
            {
                // Uniform, centred, edges on whole pixels.
                double k = Math.Min(viewW / (double)p.Width, viewH / (double)p.Height);
                double x = (viewW - p.Width * k) / 2, y = (viewH - p.Height * k) / 2;
                int left = (int)Math.Round(x), top = (int)Math.Round(y);
                picture = new PixelRect(left, top, Math.Max(1, (int)Math.Round(x + p.Width * k) - left), Math.Max(1, (int)Math.Round(y + p.Height * k) - top));
            }
            else
            {
                // One picture pixel per device pixel, at the top left, scrolled.
                _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, p.Width - viewW));
                _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, p.Height - viewH));
                picture = new PixelRect(-_scrollX, -_scrollY, p.Width, p.Height);
            }
        }
        _pictureRect = picture;

        _canvas.Arrange(new Rect(0, 0, viewW / s, viewH / s));
        _updatingBars = true;
        try
        {
            ArrangeBar(_vbar, vbar, new Rect(viewW / s, 0, barW / s, viewH / s), (_picture?.Height ?? 0) - viewH, viewH, _scrollY);
            ArrangeBar(_hbar, hbar, new Rect(0, viewH / s, viewW / s, barH / s), (_picture?.Width ?? 0) - viewW, viewW, _scrollX);
        }
        finally { _updatingBars = false; }

        if (_still.IsVisible)
        {
            Canvas.SetLeft(_still, picture.X / s);
            Canvas.SetTop(_still, picture.Y / s);
            _still.Width = picture.Width / s;
            _still.Height = picture.Height / s;
        }

        // The presenter covers the visible part of the picture, never more: a native window ignores clipping.
        var visible = picture.Intersect(new PixelRect(0, 0, viewW, viewH));
        if (_presenter is { } presenter)
        {
            bool show = visible.Width > 0 && visible.Height > 0 && !(_videoSurfaceBlocked && presenter.IsNativeWindow);
            var control = presenter.Control;
            if (control.IsVisible != show) control.IsVisible = show;
            control.Arrange(show ? new Rect(visible.X / s, visible.Y / s, visible.Width / s, visible.Height / s) : default);
            _presenterSize = show ? visible.Size : default;
            // The whole picture relative to the control, which sits at the visible part's corner.
            _presenterDestination = new PixelRect(picture.X - visible.X, picture.Y - visible.Y, picture.Width, picture.Height);
            QueuePresenterLayout();
        }
        return finalSize;
    }

    private static void ArrangeBar(ScrollBar bar, bool shown, Rect rect, int maximum, int viewport, int value)
    {
        if (bar.IsVisible != shown) bar.IsVisible = shown;
        if (!shown) { bar.Arrange(default); return; }
        bar.Minimum = 0;
        bar.Maximum = Math.Max(0, maximum);
        bar.ViewportSize = viewport;
        bar.LargeChange = Math.Max(1, viewport * 0.9);
        bar.Value = Math.Clamp(value, 0, Math.Max(0, maximum));
        bar.Arrange(rect);
    }

    private void OnScrolled(bool horizontal, double value)
    {
        if (_updatingBars) return;
        if (horizontal) _scrollX = (int)Math.Round(value);
        else _scrollY = (int)Math.Round(value);
        InvalidateArrange();
    }

    /// <summary>Tells the presenter where the picture lies, once layout has moved its control (coalesced).</summary>
    private void QueuePresenterLayout()
    {
        if (_presenterLayoutQueued) return;
        _presenterLayoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _presenterLayoutQueued = false;
            _presenter?.SetLayout(_presenterSize, _presenterDestination);
        }, DispatcherPriority.Render);
    }

    // ------------------------------------------------------------------ pointer

    /// <summary>The picture pixel under <paramref name="p"/> (canvas coordinates), rounded; false while there is no picture.</summary>
    private bool TryMapToPicture(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_picture is not { } pic || _pictureRect.Width <= 0 || _pictureRect.Height <= 0) return false;
        double px = p.X * _scale - _pictureRect.X, py = p.Y * _scale - _pictureRect.Y;
        x = (int)Math.Round(px * pic.Width / _pictureRect.Width);
        y = (int)Math.Round(py * pic.Height / _pictureRect.Height);
        return true;
    }

    private void SendPointer(Point p)
    {
        _lastPointer = p;
        if (_agentInput != null)
        {
            SendAgentPointer(p);
            return;
        }
        if (_client == null || !TryMapToPicture(p, out var x, out var y)) return;
        _client.SendPointerEvent(x, y, _buttons);
    }

    /// <summary>
    /// Agent-only sessions: the pixel of the picture under <paramref name="p"/> (the one it falls in, clamped to the
    /// picture while a drag goes past its edge) and the buttons, to the agent.
    /// </summary>
    private void SendAgentPointer(Point p)
    {
        if (_picture is not { } pic || _pictureRect.Width <= 0 || _pictureRect.Height <= 0) return;
        double px = p.X * _scale - _pictureRect.X, py = p.Y * _scale - _pictureRect.Y;
        int x = Math.Clamp((int)Math.Floor(px * pic.Width / _pictureRect.Width), 0, pic.Width - 1);
        int y = Math.Clamp((int)Math.Floor(py * pic.Height / _pictureRect.Height), 0, pic.Height - 1);
        _agentPointer = (x, y);
        _agentInput!.TrySendPointer(x, y, pic.Width, pic.Height, _agentButtons);
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _pointerInside = true;
        ApplyCursor();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => _pointerInside = false;

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        // Chorded presses can arrive as moves: the buttons always come from the event's state.
        TakeButtons(point.Properties);
        SendPointer(point.Position);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(_canvas);
        TakeButtons(point.Properties);
        e.Pointer.Capture(_canvas);
        SendPointer(point.Position);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        TakeButtons(point.Properties);
        SendPointer(point.Position);
        if (_buttons == 0 && _agentButtons == 0) e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void TakeButtons(PointerPointProperties p)
    {
        _buttons = (byte)((p.IsLeftButtonPressed ? PointerButton.Left : 0) | (p.IsMiddleButtonPressed ? PointerButton.Middle : 0)
            | (p.IsRightButtonPressed ? PointerButton.Right : 0) | (p.IsXButton1Pressed ? PointerButton.Back : 0));
        _agentButtons = (p.IsLeftButtonPressed ? AgentButton.Left : 0) | (p.IsMiddleButtonPressed ? AgentButton.Middle : 0)
            | (p.IsRightButtonPressed ? AgentButton.Right : 0) | (p.IsXButton1Pressed ? AgentButton.Back : 0)
            | (p.IsXButton2Pressed ? AgentButton.Forward : 0);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var position = e.GetPosition(_canvas);
        // Avalonia gives notches (Windows' WHEEL_DELTA / 120) and counts a tilt to the right as negative.
        if (e.Delta.Y != 0) SendWheel((int)Math.Round(e.Delta.Y * 120), horizontal: false, position);
        if (e.Delta.X != 0) SendWheel((int)Math.Round(-e.Delta.X * 120), horizontal: true, position);
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

    /// <param name="delta">Windows wheel units: 120 per notch, positive up or right.</param>
    /// <param name="position">Canvas coordinates.</param>
    public void SendWheel(int delta, bool horizontal, Point position)
    {
        if (!IsAttached || delta == 0 || !TryMapToPicture(position, out var x, out var y)) return;
        if (TrySendSmoothScroll(delta, horizontal, x, y)) return;
        if (_client == null) return; // an agent-only session whose agent does not scroll
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

    /// <summary>Windows "wheel scroll lines" setting; a page (WHEEL_PAGESCROLL) is approximated as 10 lines.</summary>
    private static double WheelScrollLines =>
        !SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out uint lines, 0) ? 3 : lines == uint.MaxValue ? 10 : lines;

    /// <summary>Windows "wheel scroll characters" setting for a tilted wheel (default 3).</summary>
    private static double WheelScrollChars =>
        SystemParametersInfo(SPI_GETWHEELSCROLLCHARS, 0, out uint chars, 0) && chars > 0 && chars < 1000 ? chars : 3;

    // ------------------------------------------------------------------ keyboard

    /// <summary>Called by the owning window for every raw key message while this view has focus.</summary>
    public void SendKey(int vk, int scancode, bool extended, bool down)
    {
        if (_agentInput != null)
        {
            SendAgentKey(vk, scancode, extended, down);
            return;
        }
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

    /// <summary>Agent-only sessions: the key as the guest's keyboard would send it; released with what it was pressed with.</summary>
    private void SendAgentKey(int vk, int scancode, bool extended, bool down)
    {
        int scan = AgentScanCode(vk, scancode, extended);
        int id = scan != 0 ? scan : 0x10000 | vk;
        if (down) _agentKeysDown[id] = (scan, vk);
        else if (_agentKeysDown.Remove(id, out var pressed)) (scan, vk) = pressed;
        _agentInput!.TrySendKey(scan, vk, down);
    }

    /// <summary>
    /// The set 1 make code (0xE000 added for extended keys) the agent injects for a key message's scan code and extended
    /// flag. Two keys report something other than what the keyboard sends: Num Lock comes with the extended flag although
    /// its make code is a plain 0x45, and Pause reports 0x45 without it while its real sequence (E1 1D 45) has no single
    /// code, so it travels as a virtual key alone (0).
    /// </summary>
    public static int AgentScanCode(int vk, int scancode, bool extended) => vk switch
    {
        VK_PAUSE => 0,
        VK_NUMLOCK => 0x45,
        _ => scancode is > 0 and <= 0xFF ? (extended ? 0xE000 : 0) | scancode : 0,
    };

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
        if (_agentInput != null)
        {
            foreach (var (scan, vk) in _agentKeysDown.Values) _agentInput.TrySendKey(scan, vk, false);
            _agentKeysDown.Clear();
            if (_agentButtons != 0 && _picture is { } picture)
                _agentInput.TrySendPointer(_agentPointer.X, _agentPointer.Y, picture.Width, picture.Height, 0);
            _agentButtons = 0;
            _buttons = 0;
            return;
        }
        if (_client == null) { _pressedKeys.Clear(); return; }
        foreach (var k in _pressedKeys.Values)
            _client.SendKeyEvent(k.Keysym, false, k.Scancode);
        _pressedKeys.Clear();
        if (_buttons != 0)
        {
            _buttons = 0;
            if (_pointerInside) SendPointer(_lastPointer);
        }
    }

    // ------------------------------------------------------------------ screenshot

    /// <summary>Copies the picture on screen (what the presenter shows, else the framebuffer) for screenshots. UI thread.</summary>
    public PictureSnapshot? Snapshot()
    {
        if (!IsAttached) return null;
        if (_presenter?.Capture(out int w, out int h) is { } shown && w > 0 && h > 0) return new PictureSnapshot(shown, w, h);
        if (_client == null) return null; // agent only, and no frame yet
        var fb = _client.Framebuffer;
        byte[] pixels;
        int fw, fh;
        lock (fb.SyncRoot) { pixels = fb.CopyOut(fb.Bounds); fw = fb.Width; fh = fb.Height; }
        if (fw <= 0 || fh <= 0) return null;
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new PictureSnapshot(pixels, fw, fh);
    }

    // ------------------------------------------------------------------ UI Automation

    /// <summary>What the view shows, for the automation peer's value (and UI tests).</summary>
    internal string Describe()
    {
        string picture = _picture is { } p ? $"{p.Width}x{p.Height}" : "none";
        string session = _client != null ? "VNC" : _agentInput != null ? "agent" : "detached";
        string presenter = _presenterKind switch { PresenterKind.SwapChain => "swapchain", PresenterKind.OpenGl => "opengl", _ => "bitmap" };
        var rect = _pictureRect;
        return $"session {session}; picture {picture}; mode {(_scaleMode == ScaleMode.Fit ? "fit" : "1:1")}; presenter {presenter}" +
               $"; shown at {rect.X},{rect.Y} {rect.Width}x{rect.Height}; scroll {_scrollX},{_scrollY}; surface {(_videoSurfaceBlocked ? "blocked" : "shown")}; fps {Fps:0}";
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new VncViewAutomationPeer(this);
}
