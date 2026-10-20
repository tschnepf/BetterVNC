using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using BetterVnc.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Question 2: how should the agent's H.264 stream reach the screen in Avalonia? A pre-encoded stream (with the frame
/// number drawn in) is decoded at 60 fps, as VideoStreamPlayer does, and each picture goes one of these ways:
///   bitmap          software decode, CPU NV12->BGRX, Avalonia WriteableBitmap (today's "-> WPF" fallback)
///   sw-swapchain    software decode, NV12 upload + shader, today's VideoRenderer swap chain in a NativeControlHost child window
///   dxva-swapchain  DXVA decode, GPU copy + shader, the same swap chain (today's main path)
///   composition     software decode, CPU NV12->BGRX, upload into a shared D3D11 texture that Avalonia's compositor imports
/// </summary>
internal static class VideoTest
{
    public static async Task<int> RunAsync()
    {
        string path = Program.Opt("--path") ?? "dxva-swapchain";
        var (w, h) = Program.OptSize("--size", (1280, 720));
        int seconds = Program.OptInt("--seconds", 10), kbps = Program.OptInt("--kbps", 8000);
        const int fps = 60;
        var stream = VideoStream.Encode(w, h, fps, kbps, out var encoderInfo);

        IVideoView view = path switch
        {
            "bitmap" => new BitmapVideoView(w, h),
            "sw-swapchain" or "dxva-swapchain" => new SwapChainVideoView(),
            "composition" => new CompositionVideoView(w, h),
            _ => throw new ArgumentException("--path bitmap|sw-swapchain|dxva-swapchain|composition"),
        };
        var window = await Program.ShowTestWindowAsync("video test", view.Control, w, h);
        await view.InitAsync();
        var origin = Program.ScreenPoint(view.Control, new Point(0, 0));
        int block = Marker.BlockFor(w);
        using var probe = ScreenProbe.TryCreate(origin.X + 1, origin.Y + 1);
        probe?.Watch(Program.MarkerPoints(origin, block, 1.0));
        probe?.Start();

        var run = new Run($"video {path} {w}x{h}@{fps} {kbps / 1000.0:0.#} Mbit/s", Run.GetCurrentThreadId());
        view.Run = run;
        string decoderInfo = "";
        await Task.Run(() =>
        {
            Run.NameThisThread("decode");
            using var decoder = new H264Decoder(path == "dxva-swapchain" ? view.Device : null);
            int refused = 0;
            void Decode(EncodedFrame f) => decoder.Decode(f.Data, f.TimestampMicros, (in DecodedFrame picture) =>
            {
                int marker = (int)Math.Round(picture.TimestampMicros * fps / 1e6);
                if (!view.Present(in picture, marker)) refused++;
            });
            var clock = Stopwatch.StartNew();
            long interval = Stopwatch.Frequency / fps, next = clock.ElapsedTicks;
            int index = 0;
            void PlayFor(double s)
            {
                long end = clock.ElapsedTicks + (long)(s * Stopwatch.Frequency);
                while (clock.ElapsedTicks < end)
                {
                    while (clock.ElapsedTicks < next) Thread.Sleep(1);
                    next += interval;
                    Decode(stream[index++ % stream.Count]);
                }
            }
            PlayFor(2); // warm-up
            run.Begin();
            probe?.Watch(Program.MarkerPoints(origin, block, 1.0));
            PlayFor(seconds);
            decoderInfo = $"decoder {decoder.Name} ({(decoder.IsHardware ? "hardware" : "software")}{(decoder.HardwareFailure is { } why ? ", hardware refused: " + why : "")}); presenter refused {refused} frames";
        });
        await Task.Delay(300);
        Console.WriteLine(run.End(probe, $"{encoderInfo}; {decoderInfo}; {view.Describe()}"));
        window.Close();
        view.Dispose();
        return 0;
    }
}

internal interface IVideoView : IDisposable
{
    Control Control { get; }
    Run? Run { set; }
    /// <summary>The device a DXVA decoder should use; null when the view has none.</summary>
    D3D11VideoDevice? Device { get; }
    Task InitAsync();
    /// <summary>Decode thread, inside the decoder's callback. False when the view cannot show this picture.</summary>
    bool Present(in DecodedFrame picture, int marker);
    string Describe();
}

/// <summary>Five seconds of screen-like content with the frame number drawn in, encoded once with the agent's encoder.</summary>
internal static class VideoStream
{
    public static List<EncodedFrame> Encode(int width, int height, int fps, int kbps, out string info)
    {
        int count = fps * 5, block = Marker.BlockFor(width);
        using var encoder = new H264Encoder(width, height, fps, kbps, preferHardware: true);
        var frames = new List<EncodedFrame>();
        var bgra = new byte[width * height * 4];
        for (int t = 0; t < count; t++)
        {
            Render(bgra, width, height, t);
            Marker.Draw(bgra, width * 4, block, t);
            var deadline = Environment.TickCount64 + 2000;
            while (!encoder.WantsInput)
            {
                encoder.Pump(frames);
                if (Environment.TickCount64 > deadline) throw new TimeoutException("encoder never asked for input");
                Thread.Sleep(1);
            }
            if (t == 0) encoder.ForceKeyframe();
            encoder.Encode(bgra, width * 4, t * 1_000_000L / fps);
            encoder.Pump(frames);
        }
        encoder.Drain(frames);
        info = $"stream {frames.Count} frames from {encoder.Name}, {frames.Sum(f => (long)f.Data.Length) * 8.0 / 5 / 1e6:0.0} Mbit/s";
        return frames;
    }

    private static void Render(byte[] bgra, int w, int h, int t)
    {
        Parallel.For(0, h, y =>
        {
            int scrolled = y + t * 3;
            int row = scrolled / 14, inRow = scrolled % 14;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool glyph = inRow < 10 && x % 9 < 7 && ((Hash(row, x / 9) & 3) != 0) && ((Hash(row * 7 + inRow, x) & 1) == 0);
                byte b = (byte)(200 + y * 40 / h), g = (byte)(205 + x * 30 / w), r = 210;
                if (glyph) { b = 40; g = 40; r = 50; }
                int bx = (t * 8) % (w + 300) - 300;
                if (x >= bx && x < bx + 300 && y >= h / 6 && y < h / 6 + 200) { b = 230; g = 120; r = 40; }
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
        });
    }

    private static int Hash(int a, int b) => (int)((uint)(a * 73856093 ^ b * 19349663) >> 7);
}

/// <summary>Software-decoded pictures converted on the CPU and drawn through an Avalonia WriteableBitmap (newest wins).</summary>
internal sealed class BitmapVideoView : IVideoView
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly WriteableBitmap _bitmap;
    private readonly int _w, _h;
    private readonly ConcurrentBag<byte[]> _pool = new();
    private (byte[] Pixels, int Marker)? _pending;
    private readonly object _lock = new();

    public BitmapVideoView(int w, int h)
    {
        _w = w; _h = h;
        _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;
    }

    public Control Control => _image;
    public Run? Run { private get; set; }
    public D3D11VideoDevice? Device => null;
    public Task InitAsync() => Task.CompletedTask;

    public bool Present(in DecodedFrame picture, int marker)
    {
        if (picture.IsTexture || picture.Width != _w || picture.Height != _h) return false;
        var buffer = _pool.TryTake(out var b) ? b : new byte[_w * _h * 4];
        picture.ToBgrx(buffer);
        bool post;
        lock (_lock)
        {
            if (_pending is { } old) _pool.Add(old.Pixels);
            post = _pending == null;
            _pending = (buffer, marker);
        }
        if (post) Dispatcher.UIThread.Post(Draw, DispatcherPriority.Render);
        return true;
    }

    private unsafe void Draw()
    {
        long t0 = Stopwatch.GetTimestamp();
        (byte[] Pixels, int Marker) frame;
        lock (_lock)
        {
            if (_pending is not { } p) return;
            frame = p;
            _pending = null;
        }
        using (var locked = _bitmap.Lock())
            fixed (byte* src = frame.Pixels)
                for (int y = 0; y < _h; y++)
                    Buffer.MemoryCopy(src + y * _w * 4, (byte*)locked.Address + y * locked.RowBytes, _w * 4, _w * 4);
        _pool.Add(frame.Pixels);
        _image.InvalidateVisual();
        Run?.UiCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Run?.Submit(frame.Marker);
    }

    public string Describe() => "software decode -> CPU BGRX -> WriteableBitmap";
    public void Dispose() { }
}

/// <summary>
/// Today's VideoRenderer (flip-model swap chain) presenting into a child window that an Avalonia NativeControlHost
/// creates and places. The renderer's layout follows the control's size.
/// </summary>
internal sealed class SwapChainVideoView : NativeControlHost, IVideoView
{
    private const string ClassName = "BetterVncSpikeVideoSurface";
    private static bool s_registered;
    private readonly D3D11VideoDevice _device = D3D11VideoDevice.Create();
    private readonly VideoRenderer _renderer;
    private IntPtr _hwnd;

    public SwapChainVideoView()
    {
        _renderer = new VideoRenderer(_device);
        SizeChanged += (_, _) => UpdateLayout();
    }

    public Control Control => this;
    public Run? Run { private get; set; }
    public D3D11VideoDevice? Device => _device;
    public IntPtr Hwnd => _hwnd;
    public VideoRenderer Renderer => _renderer;

    public Task InitAsync() { UpdateLayout(); return Task.CompletedTask; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var instance = Native.GetModuleHandle(null);
        if (!s_registered)
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Native.GetProcAddress(Native.GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception();
            s_registered = true;
        }
        _hwnd = Native.CreateWindowEx(0x4 /* WS_EX_NOPARENTNOTIFY */, ClassName, "", 0x40000000 | 0x04000000 /* WS_CHILD | WS_CLIPSIBLINGS */,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();
        _renderer.SetWindow(_hwnd);
        Dispatcher.UIThread.Post(UpdateLayout, DispatcherPriority.Render);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _renderer.SetWindow(IntPtr.Zero);
        Native.DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    public void UpdateLayout()
    {
        if (_hwnd == IntPtr.Zero) return;
        double s = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = (int)Math.Round(Bounds.Width * s), h = (int)Math.Round(Bounds.Height * s);
        _renderer.SetLayout(w, h, 0, 0, w, h);
    }

    public bool Present(in DecodedFrame picture, int marker)
    {
        if (!_renderer.Present(in picture)) return false;
        Run?.Submit(marker);
        return true;
    }

    /// <summary>A BGRX picture through the same swap chain (for the placement checks).</summary>
    public bool Present(byte[] bgrx, int width, int height) => _renderer.Present(bgrx, width, height);

    public string Describe() => $"VideoRenderer swap chain in a NativeControlHost child window; {_device.AdapterName}, GPU thread priority {_device.GpuThreadPriority}";

    public void Dispose() { _renderer.Dispose(); _device.Dispose(); }
}

/// <summary>
/// Pictures uploaded into one of three shared D3D11 textures (keyed mutex) and handed to Avalonia's compositor with
/// CompositionDrawingSurface.UpdateWithKeyedMutexAsync, so the video is an ordinary part of the visual tree.
/// </summary>
internal sealed class CompositionVideoView : Control, IVideoView
{
    private readonly int _w, _h;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private readonly List<(ID3D11Texture2D Texture, IDXGIKeyedMutex Mutex, ICompositionImportedGpuImage Image)> _slots = new();
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private int _next;
    private byte[]? _bgrx;
    private (int Slot, int Marker)? _pending;
    private readonly object _lock = new();
    private string _info = "";
    private int _skipped;

    public CompositionVideoView(int w, int h) { _w = w; _h = h; }

    public Control Control => this;
    public Run? Run { private get; set; }
    public D3D11VideoDevice? Device => null;

    public async Task InitAsync()
    {
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor ?? throw new InvalidOperationException("no compositor");
        var interop = await compositor.TryGetCompositionGpuInterop() ?? throw new InvalidOperationException("no GPU interop (software rendering?)");
        string handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
        if (!interop.SupportedImageHandleTypes.Contains(handleType))
            throw new InvalidOperationException("compositor does not import " + handleType + "; it takes: " + string.Join(", ", interop.SupportedImageHandleTypes));

        // The textures must live on the compositor's GPU.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? match = null;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            var luid = adapter.Description1.Luid;
            var bytes = BitConverter.GetBytes(luid.LowPart).Concat(BitConverter.GetBytes(luid.HighPart)).ToArray();
            if (interop.DeviceLuid is { } want && want.SequenceEqual(bytes)) { match = adapter; break; }
            adapter.Dispose();
        }
        if (match == null) throw new InvalidOperationException("no DXGI adapter matches the compositor's LUID");
        D3D11.D3D11CreateDevice(match, DriverType.Unknown, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
            out _device, out _context).CheckError();
        _info = $"compositor imports [{string.Join(", ", interop.SupportedImageHandleTypes)}]; device on {match.Description1.Description}";
        match.Dispose();

        for (int i = 0; i < 3; i++)
        {
            var texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
            });
            using var resource = texture.QueryInterface<IDXGIResource>();
            var image = interop.ImportImage(new PlatformHandle(resource.SharedHandle, handleType),
                new PlatformGraphicsExternalImageProperties { Width = _w, Height = _h, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm, TopLeftOrigin = true });
            _slots.Add((texture, texture.QueryInterface<IDXGIKeyedMutex>(), image));
        }
        _surface = compositor.CreateDrawingSurface();
        _visual = compositor.CreateSurfaceVisual();
        _visual.Surface = _surface;
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        SizeChanged += (_, _) => { if (_visual != null) _visual.Size = new Vector(Bounds.Width, Bounds.Height); };
        ElementComposition.SetElementChildVisual(this, _visual);
    }

    public unsafe bool Present(in DecodedFrame picture, int marker)
    {
        if (picture.IsTexture || picture.Width != _w || picture.Height != _h || _context == null) return false;
        _bgrx ??= new byte[_w * _h * 4];
        picture.ToBgrx(_bgrx);
        int slot = _next;
        var s = _slots[slot];
        // The compositor gives the texture back by releasing key 0; a slot still in use means the screen is behind: skip.
        if (AcquireSync(s.Mutex, 0, 50) != 0) { Interlocked.Increment(ref _skipped); return true; }
        fixed (byte* p = _bgrx) _context.UpdateSubresource(s.Texture, 0, null, (IntPtr)p, (uint)(_w * 4), 0);
        s.Mutex.ReleaseSync(1);
        _context.Flush();
        _next = (slot + 1) % _slots.Count;
        bool post;
        lock (_lock)
        {
            post = _pending == null;
            _pending = (slot, marker);
        }
        if (post) Dispatcher.UIThread.Post(Show, DispatcherPriority.Render);
        return true;
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync (vtable slot 8) with its HRESULT: Vortice's wrapper hides WAIT_TIMEOUT (0x102).</summary>
    private static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint ms)
    {
        var p = mutex.NativePointer;
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)(*(*(void***)p + 8)))(p, key, ms);
    }

    private void Show()
    {
        (int Slot, int Marker) frame;
        lock (_lock)
        {
            if (_pending is not { } p) return;
            frame = p;
            _pending = null;
        }
        _ = _surface!.UpdateWithKeyedMutexAsync(_slots[frame.Slot].Image, 1, 0);
        Run?.Submit(frame.Marker);
    }

    public string Describe() => $"software decode -> CPU BGRX -> shared D3D11 texture -> CompositionDrawingSurface (keyed mutex); {_info}; {_skipped} frames skipped (slot busy)";

    public void Dispose()
    {
        foreach (var s in _slots) { s.Mutex.Dispose(); s.Texture.Dispose(); }
        _context?.Dispose();
        _device?.Dispose();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top} {Right - Left}x{Bottom - Top}"; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);

    /// <summary>The colour on screen at a device-pixel point (GDI; frozen or black while the PC is locked).</summary>
    public static (byte R, byte G, byte B) ScreenColor(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        uint c = GetPixel(dc, x, y);
        ReleaseDC(IntPtr.Zero, dc);
        return ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }
}
