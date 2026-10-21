using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using BetterVnc.Media;
using BetterVnc.Protocol;
using PixelRect = Avalonia.PixelRect;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// The fallback where the swap chain is unavailable (docs/AVALONIA-SPIKE.md, section 8): OpenGL inside Avalonia's
/// compositor. The framebuffer is a texture created once per size that only the changed rectangles are uploaded into
/// (glTexSubImage2D with GL_UNPACK_ROW_LENGTH); stream pictures arrive converted to BGRX and go into a second texture,
/// drawn over the framebuffer or instead of it. Avalonia composes it like any control, so overlays draw over it.
/// Gives up (<see cref="Failed"/>) when Avalonia has no OpenGL context to offer.
/// </summary>
internal sealed unsafe class GlPresenter : OpenGlControlBase, IPicturePresenter
{
    private const int GL_TEXTURE_2D = 0x0DE1, GL_RGBA = 0x1908, GL_BGRA_EXT = 0x80E1, GL_UNSIGNED_BYTE = 0x1401, GL_LINEAR = 0x2601;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_UNPACK_ROW_LENGTH = 0x0CF2, GL_ARRAY_BUFFER = 0x8892, GL_STATIC_DRAW = 0x88E4, GL_FLOAT = 0x1406, GL_TRIANGLE_STRIP = 5;
    private const int GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30, GL_COLOR_BUFFER_BIT = 0x4000, GL_TEXTURE0 = 0x84C0;

    private readonly CpuPictureSource _source = new();
    private readonly Action _frameShown;
    private readonly DispatcherTimer _initCheck;
    private int _redrawQueued;
    private bool _initialized, _failed;
    private int _program, _vbo, _vao, _uRect, _uUv;
    private int _fbTexture, _fbWidth, _fbHeight;
    private int _streamTexture, _streamTexWidth, _streamTexHeight, _streamWidth, _streamHeight;
    private (int X, int Y)? _streamOffset;
    private bool _streamOn;
    private int _internalFormat = GL_BGRA_EXT;
    private delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void> _texSubImage2D;
    private delegate* unmanaged[Stdcall]<int, int, void> _pixelStorei;
    private delegate* unmanaged[Stdcall]<int, float, float, float, float, void> _uniform4f;
    private PixelSize _size;
    private PixelRect _destination;

    /// <param name="frameShown">Called for every picture that reached the screen.</param>
    public GlPresenter(Action frameShown)
    {
        _frameShown = frameShown;
        IsHitTestVisible = false;
        Focusable = false;
        _source.Changed += QueueRedraw;
        // OpenGlControlBase says nothing when Avalonia has no OpenGL to give it: it just never initialises.
        _initCheck = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _initCheck!.Stop();
            if (!_initialized) Fail("Avalonia offers no OpenGL context here");
        });
        _initCheck.Stop();
    }

    public Control Control => this;
    public bool IsNativeWindow => false;
    public string PathName => "OpenGL";
    public string Description { get; private set; } = "OpenGL (not initialised yet)";
    public D3D11VideoDevice? VideoDevice => null;
    public event Action<string>? Failed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_initialized) _initCheck.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _initCheck.Stop();
        base.OnDetachedFromVisualTree(e);
    }

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
        if (size == _size && destination == _destination) return;
        _size = size;
        _destination = destination;
        RequestNextFrameRendering();
    }

    private void QueueRedraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _redrawQueued, 0);
            RequestNextFrameRendering();
        }, DispatcherPriority.Render);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _texSubImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, IntPtr, void>)gl.GetProcAddress("glTexSubImage2D");
            _pixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glPixelStorei");
            _uniform4f = (delegate* unmanaged[Stdcall]<int, float, float, float, float, void>)gl.GetProcAddress("glUniform4f");
            if (_texSubImage2D == null || _pixelStorei == null || _uniform4f == null) throw new InvalidOperationException("glTexSubImage2D, glPixelStorei or glUniform4f is missing");
            var version = gl.ContextInfo.Version;
            bool es = version.Type == GlProfileType.OpenGLES;
            // ANGLE (OpenGL ES) takes BGRA as the internal format too (EXT_texture_format_BGRA8888); desktop OpenGL wants RGBA.
            _internalFormat = es ? GL_BGRA_EXT : GL_RGBA;
            string header = es ? "#version 100\nprecision mediump float;\n" : "#version 120\n";
            int vs = gl.CreateShader(GL_VERTEX_SHADER);
            string? err = gl.CompileShaderAndGetError(vs, header +
                "attribute vec2 aPos; uniform vec4 uRect; uniform vec4 uUv; varying vec2 vUv;\n" +
                "void main() { vUv = uUv.xy + aPos * uUv.zw; gl_Position = vec4(uRect.x + aPos.x * uRect.z, uRect.y - aPos.y * uRect.w, 0.0, 1.0); }");
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
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            _uRect = gl.GetUniformLocationString(_program, "uRect");
            _uUv = gl.GetUniformLocationString(_program, "uUv");

            if (gl.IsGenVertexArraysAvailable) { _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao); }
            _vbo = gl.GenBuffer();
            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
            var quad = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 };
            fixed (float* q = quad) gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(quad.Length * 4), (IntPtr)q, GL_STATIC_DRAW);
            Description = $"OpenGL in Avalonia's compositor: {gl.Renderer} / {gl.Version}";
            _initialized = true;
            _initCheck.Stop();
            _source.InvalidateAll();
        }
        catch (Exception ex)
        {
            Fail("OpenGL setup failed: " + ex.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_fbTexture != 0) gl.DeleteTexture(_fbTexture);
        if (_streamTexture != 0) gl.DeleteTexture(_streamTexture);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_program != 0) gl.DeleteProgram(_program);
        _fbTexture = _streamTexture = _vbo = _vao = _program = 0;
        _fbWidth = _fbHeight = _streamTexWidth = _streamTexHeight = 0;
        _initialized = false;
    }

    protected override void OnOpenGlLost()
    {
        _fbTexture = _streamTexture = _vbo = _vao = _program = 0;
        _fbWidth = _fbHeight = _streamTexWidth = _streamTexHeight = 0;
        _initialized = false;
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_initialized) return;
        bool changed = UploadFramebuffer(gl);
        changed |= _source.TakeStream((bgrx, w, h, offset) => UploadStream(gl, bgrx, w, h, offset));

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling)), height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.UseProgram(_program);
        gl.ActiveTexture(GL_TEXTURE0);
        if (_vao != 0) gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 8, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);

        var clip = new Rect(0, 0, width, height);
        var dest = new Rect(_destination.X, _destination.Y, _destination.Width, _destination.Height);
        bool fbShown = _fbTexture != 0 && (!_streamOn || _streamOffset != null);
        if (fbShown) DrawQuad(gl, _fbTexture, dest, clip, width, height);
        if (_streamOn && _streamTexture != 0)
        {
            if (_streamOffset is { } at && _fbTexture != 0)
            {
                double sx = dest.Width / _fbWidth, sy = dest.Height / _fbHeight;
                var part = new Rect(dest.X + at.X * sx, dest.Y + at.Y * sy, _streamWidth * sx, _streamHeight * sy);
                DrawQuad(gl, _streamTexture, part, clip.Intersect(dest), width, height, _streamWidth / (double)_streamTexWidth, _streamHeight / (double)_streamTexHeight);
            }
            else DrawQuad(gl, _streamTexture, dest, clip, width, height, _streamWidth / (double)_streamTexWidth, _streamHeight / (double)_streamTexHeight);
        }
        if (changed) _frameShown();
    }

    /// <summary>Draws a texture (the part <paramref name="uMax"/>×<paramref name="vMax"/> of it holds the picture) into <paramref name="dest"/>, clipped.</summary>
    private void DrawQuad(GlInterface gl, int texture, Rect dest, Rect clip, int width, int height, double uMax = 1, double vMax = 1)
    {
        var shown = dest.Intersect(clip);
        if (shown.Width <= 0 || shown.Height <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
        double u0 = (shown.X - dest.X) / dest.Width * uMax, v0 = (shown.Y - dest.Y) / dest.Height * vMax;
        double uw = shown.Width / dest.Width * uMax, vh = shown.Height / dest.Height * vMax;
        _uniform4f(_uRect, (float)(shown.X / width * 2 - 1), (float)(1 - shown.Y / height * 2), (float)(shown.Width / width * 2), (float)(shown.Height / height * 2));
        _uniform4f(_uUv, (float)u0, (float)v0, (float)uw, (float)vh);
        gl.BindTexture(GL_TEXTURE_2D, texture);
        gl.DrawArrays(GL_TRIANGLE_STRIP, 0, (IntPtr)4);
    }

    private bool UploadFramebuffer(GlInterface gl)
    {
        var rects = _source.TakeDirty(out bool all);
        var fb = _source.Framebuffer;
        if (fb == null)
        {
            if (_fbTexture != 0) { gl.DeleteTexture(_fbTexture); _fbTexture = 0; _fbWidth = _fbHeight = 0; return true; }
            return false;
        }
        if (!all && rects.Length == 0) return false;
        lock (fb.SyncRoot)
        {
            int w = fb.Width, h = fb.Height;
            if (w <= 0 || h <= 0) return false;
            if (_fbTexture == 0 || w != _fbWidth || h != _fbHeight)
            {
                if (_fbTexture == 0) _fbTexture = CreateTexture(gl);
                gl.BindTexture(GL_TEXTURE_2D, _fbTexture);
                gl.TexImage2D(GL_TEXTURE_2D, 0, _internalFormat, w, h, 0, GL_BGRA_EXT, GL_UNSIGNED_BYTE, IntPtr.Zero);
                _fbWidth = w;
                _fbHeight = h;
                all = true;
            }
            gl.BindTexture(GL_TEXTURE_2D, _fbTexture);
            _pixelStorei(GL_UNPACK_ROW_LENGTH, w);
            fixed (byte* p = fb.Pixels)
            {
                if (all) _texSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)p);
                else
                    foreach (var r0 in rects)
                    {
                        var r = r0.Intersect(fb.Bounds);
                        if (!r.IsEmpty) _texSubImage2D(GL_TEXTURE_2D, 0, r.X, r.Y, r.Width, r.Height, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)(p + fb.Offset(r.X, r.Y)));
                    }
            }
            _pixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        }
        return true;
    }

    private void UploadStream(GlInterface gl, byte[]? bgrx, int width, int height, (int X, int Y)? offset)
    {
        if (bgrx == null) { _streamOn = false; return; }
        if (_streamTexture == 0) _streamTexture = CreateTexture(gl);
        gl.BindTexture(GL_TEXTURE_2D, _streamTexture);
        fixed (byte* p = bgrx)
        {
            if (width > _streamTexWidth || height > _streamTexHeight || width < _streamTexWidth / 2 || height < _streamTexHeight / 2)
            {
                gl.TexImage2D(GL_TEXTURE_2D, 0, _internalFormat, width, height, 0, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)p);
                _streamTexWidth = width;
                _streamTexHeight = height;
            }
            else _texSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, GL_BGRA_EXT, GL_UNSIGNED_BYTE, (IntPtr)p);
        }
        _streamWidth = width;
        _streamHeight = height;
        _streamOffset = offset;
        _streamOn = true;
    }

    private static int CreateTexture(GlInterface gl)
    {
        int texture = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, texture);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        return texture;
    }

    private void Fail(string reason)
    {
        if (_failed) return;
        _failed = true;
        Description = "OpenGL unavailable: " + reason;
        Failed?.Invoke(reason);
    }

    public void Dispose()
    {
        _initCheck.Stop();
        _source.Changed -= QueueRedraw;
    }
}
