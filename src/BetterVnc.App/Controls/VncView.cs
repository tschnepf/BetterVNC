using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.App.Input;
using BetterVnc.App.Models;
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

    /// <summary>True while the displayed picture comes from <see cref="PresentExternalFrame"/> rather than the RFB framebuffer.</summary>
    public bool IsExternalShown => _externalShown;

    private readonly object _pendingLock = new();
    private (byte[] Pixels, int Width, int Height, Action<byte[]>? Release)? _pendingExternal;

    /// <summary>
    /// Shows a decoded frame from somewhere other than RFB (the guest agent's video stream) in place of the framebuffer.
    /// The RFB framebuffer keeps being decoded underneath so <see cref="ShowFramebuffer"/> can switch back instantly.
    /// <paramref name="bgrx"/> holds width*height*4 bytes; it belongs to the view until <paramref name="release"/> is
    /// called with it (or forever when no release is given). A frame that arrives before the previous one was drawn
    /// replaces it: the newest picture wins. Safe to call from any thread.
    /// </summary>
    public void PresentExternalFrame(byte[] bgrx, int width, int height, Action<byte[]>? release = null)
    {
        if (width <= 0 || height <= 0 || bgrx.Length < width * height * 4) { release?.Invoke(bgrx); return; }
        bool queue;
        lock (_pendingLock)
        {
            if (_pendingExternal is { } superseded) superseded.Release?.Invoke(superseded.Pixels);
            queue = _pendingExternal == null;
            _pendingExternal = (bgrx, width, height, release);
        }
        if (!queue) return;
        Dispatcher.BeginInvoke(() =>
        {
            (byte[] Pixels, int Width, int Height, Action<byte[]>? Release) frame;
            lock (_pendingLock)
            {
                if (_pendingExternal is not { } pending) return;
                frame = pending;
                _pendingExternal = null;
            }
            try
            {
                if (_client == null) return;
                if (_externalBitmap == null || _externalBitmap.PixelWidth != frame.Width || _externalBitmap.PixelHeight != frame.Height)
                    _externalBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                _externalBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
                if (!_externalShown)
                {
                    _externalShown = true;
                    _image.Source = _externalBitmap;
                }
                CountFrame();
            }
            finally { frame.Release?.Invoke(frame.Pixels); }
        }, DispatcherPriority.Render);
    }

    /// <summary>Returns to the RFB framebuffer picture (repainting all of it). Safe to call from any thread.</summary>
    public void ShowFramebuffer()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_externalShown) return;
            _externalShown = false;
            _image.Source = _bitmap;
            if (_client != null) lock (_dirtyLock) _dirty.Add(_client.Framebuffer.Bounds);
            QueueRender();
        }, DispatcherPriority.Render);
    }

    private void CountFrame()
    {
        _frames++;
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
