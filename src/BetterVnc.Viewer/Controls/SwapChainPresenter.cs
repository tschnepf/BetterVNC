using Avalonia.Controls;
using BetterVnc.Media;
using BetterVnc.Protocol;
using PixelRect = Avalonia.PixelRect;
using PixelSize = Avalonia.PixelSize;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// The GPU path: the whole picture goes through one <see cref="VideoRenderer"/> swap chain in a native child window
/// (<see cref="VideoSurfaceHost"/>). Hardware-decoded stream pictures stay on the GPU; the framebuffer's changed
/// rectangles are uploaded and presented from a thread of its own, because Present waits for the display (about one
/// frame) and the UI thread must not.
/// </summary>
internal sealed unsafe class SwapChainPresenter : IPicturePresenter
{
    private readonly D3D11VideoDevice _device;
    private readonly VideoRenderer _renderer;
    private readonly VideoSurfaceHost _host = new();
    private readonly Action _frameShown;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _lock = new();
    private readonly List<RfbRect> _dirty = new();
    private bool _allDirty;            // under _lock
    private Framebuffer? _framebuffer; // under _lock
    private volatile bool _disposed;
    private int _failed;

    /// <param name="frameShown">Called (any thread) for every picture that reached the screen.</param>
    public SwapChainPresenter(Action frameShown)
    {
        _frameShown = frameShown;
        _device = D3D11VideoDevice.Create();
        try { _renderer = new VideoRenderer(_device); }
        catch { _device.Dispose(); throw; }
        // Avalonia destroys the window only once the control has left the window for good; frames then wait for a new one.
        _host.WindowChanged += hwnd => _renderer.SetWindow(hwnd);
        Description = $"GPU presentation on {_device.AdapterName} (flip-model swap chain, frame latency 1, GPU thread priority {_device.GpuThreadPriority})";
        _thread = new Thread(PresentLoop) { IsBackground = true, Name = "BetterVNC framebuffer present" };
        _thread.Start();
    }

    public Control Control => _host;
    public bool IsNativeWindow => true;
    public string PathName => "GPU";
    public string Description { get; }
    public D3D11VideoDevice? VideoDevice => _renderer.IsUsable ? _device : null;
    public event Action<string>? Failed;

    public void SetFramebuffer(Framebuffer? framebuffer)
    {
        lock (_lock)
        {
            _framebuffer = framebuffer;
            _dirty.Clear();
            _allDirty = true;
        }
        if (framebuffer == null) _renderer.ClearFramebuffer();
        else _wake.Set();
    }

    public void InvalidateFramebuffer(RfbRect[]? rects)
    {
        lock (_lock)
        {
            if (rects == null) _allDirty = true;
            else if (!_allDirty) _dirty.AddRange(rects);
        }
        _wake.Set();
    }

    /// <summary>Uploads what changed in the framebuffer and presents, as fast as the display takes it (changes coalesce while Present waits).</summary>
    private void PresentLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed) return;
            Framebuffer? fb;
            RfbRect[] rects;
            bool all;
            lock (_lock)
            {
                fb = _framebuffer;
                rects = _dirty.ToArray();
                all = _allDirty;
                _dirty.Clear();
                _allDirty = false;
            }
            if (fb == null || (rects.Length == 0 && !all)) continue;
            bool presented = _renderer.UpdateFramebuffer(target =>
            {
                lock (fb.SyncRoot)
                {
                    bool fresh = target.EnsureSize(fb.Width, fb.Height);
                    if (fb.Width <= 0 || fb.Height <= 0) return;
                    var bounds = fb.Bounds;
                    fixed (byte* pixels = fb.Pixels)
                    {
                        if (fresh || all) Copy(target, fb, pixels, bounds);
                        else foreach (var r in rects) Copy(target, fb, pixels, r.Intersect(bounds));
                    }
                }
            });
            if (presented) _frameShown();
            else if (!_renderer.IsUsable) Fail(_renderer.Failure ?? "it was shut down");
        }
    }

    private static void Copy(VideoRenderer.FramebufferTarget target, Framebuffer fb, byte* pixels, RfbRect r)
    {
        if (r.IsEmpty) return;
        target.Copy((IntPtr)(pixels + fb.Offset(r.X, r.Y)), fb.Stride, r.X, r.Y, r.Width, r.Height);
    }

    public bool PresentGpu(in DecodedFrame frame, (int X, int Y)? offset)
    {
        if (_renderer.Present(in frame, offset)) { _frameShown(); return true; }
        if (!_renderer.IsUsable) Fail(_renderer.Failure ?? "it was shut down");
        return false;
    }

    public bool PresentGpu(ReadOnlySpan<byte> bgrx, int width, int height, (int X, int Y)? offset)
    {
        if (_renderer.Present(bgrx, width, height, offset)) { _frameShown(); return true; }
        if (!_renderer.IsUsable) Fail(_renderer.Failure ?? "it was shut down");
        return false;
    }

    public void PresentCpu(byte[] bgrx, int width, int height, Action<byte[]>? release, (int X, int Y)? offset)
    {
        try { PresentGpu(bgrx.AsSpan(0, Math.Min(bgrx.Length, width * height * 4)), width, height, offset); }
        finally { release?.Invoke(bgrx); }
    }

    public void HideStream() => _renderer.HideStream();

    public void SetLayout(PixelSize size, PixelRect destination)
    {
        // The renderer draws into the window's real client size, which Avalonia derived from the control's bounds.
        var (w, h) = size.Width > 0 && size.Height > 0 ? _host.Place() : (0, 0);
        _renderer.SetLayout(w, h, destination.X, destination.Y, destination.Width, destination.Height);
        if (!_renderer.IsUsable) Fail(_renderer.Failure ?? "it was shut down");
    }

    public byte[]? Capture(out int width, out int height) => _renderer.CaptureFrame(out width, out height);

    private void Fail(string reason)
    {
        if (_disposed || Interlocked.Exchange(ref _failed, 1) == 1) return;
        Failed?.Invoke(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _thread.Join(500);
        _renderer.Dispose();
        _device.Dispose();
    }
}
