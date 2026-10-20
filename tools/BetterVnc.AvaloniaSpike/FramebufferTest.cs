using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Question 1: can Avalonia draw the RFB framebuffer fast enough? A producer thread changes a framebuffer 60 times a
/// second (<see cref="SyntheticFramebuffer"/>) and queues the dirty rectangles; the view copies them into what is shown,
/// as VncView.Render does. The screen probe says which frames reached the screen and when.
/// </summary>
internal static class FramebufferTest
{
    public static async Task<int> RunAsync()
    {
        var (fbW, fbH) = Program.OptSize("--size", (1920, 1080));
        bool full = Program.Opt("--dirty") == "full";
        string draw = Program.Opt("--draw") ?? "bitmap";
        int seconds = Program.OptInt("--seconds", 10);
        var (winW, winH) = Program.OptSize("--win", (1280, 720));

        var fb = new SyntheticFramebuffer(fbW, fbH);
        IFramebufferView view = draw switch
        {
            "gl" => new GlFramebufferView(fb),
            "swapchain" => new SwapChainFramebufferView(fb),
            _ => new BitmapFramebufferView(fb),
        };
        var window = await Program.ShowTestWindowAsync("fb test", view.Control, winW, winH);

        // The picture is fitted into the window (same 16:9 shape), so picture pixel -> device pixel is winW / fbW.
        double scale = (double)winW / fbW;
        var origin = Program.ScreenPoint(view.Control, new Point(0, 0));
        using var probe = ScreenProbe.TryCreate(origin.X + 1, origin.Y + 1);
        probe?.Watch(Program.MarkerPoints(origin, fb.Block, scale));
        probe?.Start();

        var run = new Run($"fb {draw} {fbW}x{fbH} dirty={(full ? "full" : "small")} shown in {winW}x{winH}", Run.GetCurrentThreadId());
        view.Run = run;
        fb.Start(full, view.Queue);
        await Task.Delay(2000); // warm-up
        run.Begin();
        probe?.Watch(Program.MarkerPoints(origin, fb.Block, scale));
        await Task.Delay(seconds * 1000);
        fb.Stop();
        await Task.Delay(300);
        Console.WriteLine(run.End(probe, view.Describe()));
        window.Close();
        return 0;
    }
}

internal interface IFramebufferView
{
    Control Control { get; }
    Run? Run { set; }
    /// <summary>Producer thread: these rectangles changed, up to frame <paramref name="frame"/>.</summary>
    void Queue(List<DirtyRect> rects, int frame);
    string Describe();
}

/// <summary>
/// The framebuffer presented by today's VideoRenderer swap chain in a NativeControlHost child window, the whole picture
/// uploaded on every change (VideoRenderer has no dirty-rectangle upload yet: the worst case), from the producer thread.
/// </summary>
internal sealed class SwapChainFramebufferView : IFramebufferView
{
    private readonly SyntheticFramebuffer _fb;
    private readonly SwapChainVideoView _host = new();

    public SwapChainFramebufferView(SyntheticFramebuffer fb) { _fb = fb; }

    public Control Control => _host;
    public Run? Run { private get; set; }

    public void Queue(List<DirtyRect> rects, int frame)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool ok;
        lock (_fb.SyncRoot) ok = _host.Present(_fb.Pixels, _fb.Width, _fb.Height);
        if (!ok) return;
        Run?.UiCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency); // producer thread here, not the UI thread
        Run?.Submit(frame & 0xFFFF);
    }

    public string Describe() => "VideoRenderer swap chain (NativeControlHost child window), full BGRX upload per change from the producer thread; 'UI-thread work' is the Present call";
}

/// <summary>The framebuffer drawn through an Avalonia Image and WriteableBitmap, dirty rectangles copied on the UI thread.</summary>
internal sealed class BitmapFramebufferView : IFramebufferView
{
    private readonly SyntheticFramebuffer _fb;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly WriteableBitmap _bitmap;
    private readonly List<DirtyRect> _dirty = new();
    private int _frame;
    private bool _queued;

    public BitmapFramebufferView(SyntheticFramebuffer fb)
    {
        _fb = fb;
        _bitmap = new WriteableBitmap(new PixelSize(fb.Width, fb.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
    }

    public Control Control => _image;
    public Run? Run { private get; set; }

    public void Queue(List<DirtyRect> rects, int frame)
    {
        lock (_dirty)
        {
            _dirty.AddRange(rects);
            _frame = frame;
            if (_queued) return;
            _queued = true;
        }
        Dispatcher.UIThread.Post(Render, DispatcherPriority.Render);
    }

    private unsafe void Render()
    {
        long t0 = Stopwatch.GetTimestamp();
        DirtyRect[] rects;
        int frame;
        lock (_dirty)
        {
            rects = _dirty.ToArray();
            _dirty.Clear();
            frame = _frame;
            _queued = false;
        }
        using (var locked = _bitmap.Lock())
        {
            lock (_fb.SyncRoot)
            {
                fixed (byte* src = _fb.Pixels)
                {
                    foreach (var r in rects)
                    {
                        int rowBytes = r.Width * 4;
                        for (int y = 0; y < r.Height; y++)
                            Buffer.MemoryCopy(src + ((r.Y + y) * _fb.Width + r.X) * 4, (byte*)locked.Address + (r.Y + y) * locked.RowBytes + r.X * 4, rowBytes, rowBytes);
                    }
                }
            }
        }
        _image.InvalidateVisual();
        Run?.UiCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Run?.Submit(frame & 0xFFFF);
    }

    public string Describe() => "Image + WriteableBitmap (Bgra8888, opaque), dirty rects copied under Lock(), InvalidateVisual()";
}

/// <summary>
/// The framebuffer in an OpenGL texture: created once, then only the dirty rectangles are uploaded (glTexSubImage2D with
/// GL_UNPACK_ROW_LENGTH), and a textured quad fills the control. OpenGlControlBase renders into a surface that
/// Avalonia's compositor shows.
/// </summary>
internal sealed unsafe class GlFramebufferView : OpenGlControlBase, IFramebufferView
{
    private const int GL_TEXTURE_2D = 0x0DE1, GL_BGRA_EXT = 0x80E1, GL_UNSIGNED_BYTE = 0x1401, GL_LINEAR = 0x2601;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_UNPACK_ROW_LENGTH = 0x0CF2, GL_ARRAY_BUFFER = 0x8892, GL_STATIC_DRAW = 0x88E4, GL_FLOAT = 0x1406, GL_TRIANGLE_STRIP = 5;
    private const int GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30, GL_COLOR_BUFFER_BIT = 0x4000, GL_TEXTURE0 = 0x84C0;

    private readonly SyntheticFramebuffer _fb;
    private readonly List<DirtyRect> _dirty = new();
    private int _frame = -1;
    private bool _queued;
    private int _texture, _program, _vbo, _vao;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void> _texSubImage2D;
    private delegate* unmanaged[Stdcall]<int, int, void> _pixelStorei;
    private string _info = "";
    private int _renderThread;

    public GlFramebufferView(SyntheticFramebuffer fb) { _fb = fb; }

    public Control Control => this;
    public Run? Run { private get; set; }

    public void Queue(List<DirtyRect> rects, int frame)
    {
        lock (_dirty)
        {
            _dirty.AddRange(rects);
            _frame = frame;
            if (_queued) return;
            _queued = true;
        }
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _renderThread = Run.GetCurrentThreadId();
        _texSubImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void>)gl.GetProcAddress("glTexSubImage2D");
        _pixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glPixelStorei");
        var version = gl.ContextInfo.Version;
        _info = $"{gl.Renderer} / {gl.Version} ({version.Type} {version.Major}.{version.Minor})";

        _texture = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, _texture);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        lock (_fb.SyncRoot)
            fixed (byte* p = _fb.Pixels)
                gl.TexImage2D(GL_TEXTURE_2D, 0, GL_BGRA_EXT, _fb.Width, _fb.Height, 0, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)p);

        bool es = version.Type == GlProfileType.OpenGLES;
        string header = es ? "#version 100\nprecision mediump float;\n" : "#version 120\n";
        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        string? err = gl.CompileShaderAndGetError(vs, header + "attribute vec2 aPos; varying vec2 vUv; void main() { vUv = vec2((aPos.x + 1.0) * 0.5, (1.0 - aPos.y) * 0.5); gl_Position = vec4(aPos, 0.0, 1.0); }");
        if (err != null) throw new InvalidOperationException("vertex shader: " + err);
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        err = gl.CompileShaderAndGetError(fs, header + "varying vec2 vUv; uniform sampler2D uTex; void main() { gl_FragColor = vec4(texture2D(uTex, vUv).rgb, 1.0); }");
        if (err != null) throw new InvalidOperationException("fragment shader: " + err);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.BindAttribLocationString(_program, 0, "aPos");
        err = gl.LinkProgramAndGetError(_program);
        if (err != null) throw new InvalidOperationException("link: " + err);

        if (gl.IsGenVertexArraysAvailable) { _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao); }
        _vbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        var quad = new float[] { -1, -1, 1, -1, -1, 1, 1, 1 };
        fixed (float* q = quad) gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(quad.Length * 4), (IntPtr)q, GL_STATIC_DRAW);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 8, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        long t0 = Stopwatch.GetTimestamp();
        DirtyRect[] rects;
        int frame;
        lock (_dirty)
        {
            rects = _dirty.ToArray();
            _dirty.Clear();
            frame = _frame;
            _queued = false;
        }
        gl.BindTexture(GL_TEXTURE_2D, _texture);
        if (rects.Length > 0)
        {
            _pixelStorei(GL_UNPACK_ROW_LENGTH, _fb.Width);
            lock (_fb.SyncRoot)
                fixed (byte* p = _fb.Pixels)
                    foreach (var r in rects)
                        _texSubImage2D(GL_TEXTURE_2D, 0, r.X, r.Y, r.Width, r.Height, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)(p + (r.Y * _fb.Width + r.X) * 4));
            _pixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        }
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        gl.Viewport(0, 0, (int)Math.Round(Bounds.Width * scaling), (int)Math.Round(Bounds.Height * scaling));
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.UseProgram(_program);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _texture);
        if (_vao != 0) gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 8, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.DrawArrays(GL_TRIANGLE_STRIP, 0, (IntPtr)4);
        if (rects.Length > 0 && frame >= 0)
        {
            Run?.UiCost((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            Run?.Submit(frame & 0xFFFF);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        gl.DeleteTexture(_texture);
        gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        gl.DeleteProgram(_program);
    }

    public string Describe() =>
        $"OpenGlControlBase, texture updated with glTexSubImage2D per dirty rect; {_info}; rendered on " +
        (_renderThread == 0 ? "?" : _renderThread == Program.UiThreadId ? "the UI thread" : "another thread");
}
