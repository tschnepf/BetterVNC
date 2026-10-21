using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>
/// Shows the remote picture from the GPU: a flip-model swap chain on a window, a pixel shader that converts NV12 to RGB
/// (BT.601 limited range, the same matrix as <see cref="PixelConvert.Nv12ToBgrx"/>) and scales the visible picture
/// into a destination rectangle, and Present(0) at once for every frame handed in, so the newest frame is what the
/// compositor shows next (flip-discard keeps no queue; the device's frame latency is one).
/// <para>
/// It holds up to two pictures. The <b>stream</b>: hardware-decoded frames are copied GPU-to-GPU out of the decoder's
/// surface pool into the renderer's own NV12 texture (the decoder gets its surface back at once and the picture can be
/// redrawn after a resize); system-memory NV12 (software decode) and BGRX (motion JPEG) frames are uploaded as they
/// are and converted by the same shader. The <b>framebuffer</b> (optional, see <see cref="UpdateFramebuffer"/>): the
/// RFB picture as a BGRX texture that only the changed rectangles are uploaded into. While the stream shows it covers
/// the framebuffer, or, when it shows only part of the desktop (an offset), it is drawn over that part of it.
/// </para>
/// Thread safety: every method takes one lock, so the decode thread presents while the UI thread resizes.
/// </summary>
public sealed unsafe class VideoRenderer : IDisposable
{
    private const string Shader = """
        cbuffer Params : register(b0)
        {
            float2 uvOrigin;   // top-left of the visible picture, in texture coordinates
            float2 uvSize;     // its size, in texture coordinates
            float2 uvMin;      // sampling stays inside the visible picture (decoder surfaces are padded)
            float2 uvMax;
            float2 tap;        // a quarter of a destination pixel when shrinking (four taps); zero: one tap
            uint rgb;          // 1: t2 holds BGRX; 0: t0/t1 hold NV12 luma and chroma
            uint pad;
        };
        Texture2D<float> luma : register(t0);
        Texture2D<float2> chroma : register(t1);
        Texture2D<float4> bgrx : register(t2);
        SamplerState linearClamp : register(s0);

        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VsOut VS(uint id : SV_VertexID)
        {
            float2 corner = float2(id & 1, id >> 1);
            VsOut o;
            o.pos = float4(corner.x * 2 - 1, 1 - corner.y * 2, 0, 1);
            o.uv = uvOrigin + corner * uvSize;
            return o;
        }

        float3 Fetch(float2 uv)
        {
            uv = clamp(uv, uvMin, uvMax);
            if (rgb != 0) return bgrx.Sample(linearClamp, uv).rgb;
            float y = 1.1640625 * (luma.Sample(linearClamp, uv) - 16.0 / 255.0);
            float2 c = chroma.Sample(linearClamp, uv) - 128.0 / 255.0;
            return saturate(float3(y + 1.59765625 * c.y, y - 0.390625 * c.x - 0.8125 * c.y, y + 2.015625 * c.x));
        }

        float4 PS(VsOut i) : SV_Target
        {
            if (tap.x == 0 && tap.y == 0) return float4(Fetch(i.uv), 1);
            float3 s = Fetch(i.uv + float2(-tap.x, -tap.y)) + Fetch(i.uv + float2(tap.x, -tap.y))
                     + Fetch(i.uv + float2(-tap.x, tap.y)) + Fetch(i.uv + float2(tap.x, tap.y));
            return float4(s * 0.25, 1);
        }
        """;

    private static readonly Lazy<(byte[] Vs, byte[] Ps)> Bytecode = new(() =>
        (D3DCompiler.Compile(Shader, "VS", "vs_4_0"), D3DCompiler.Compile(Shader, "PS", "ps_4_0")));

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float OriginX, OriginY, SizeX, SizeY, MinX, MinY, MaxX, MaxY, TapX, TapY;
        public uint Rgb, Pad;
    }

    private enum Source { None, Nv12Texture, Nv12Planes, Bgrx }

    /// <summary>A picture the renderer can draw: its textures and views, and where in them the picture lies.</summary>
    private sealed class Picture
    {
        public Source Kind;
        public IntPtr Texture0, Texture1, View0, View1;
        public int TextureWidth, TextureHeight;
        public int X, Y, Width, Height;
        public bool HasContent => Kind != Source.None && Width > 0 && Height > 0;
    }

    private readonly object _lock = new();
    private IntPtr _device, _context;
    private IntPtr _vs, _ps, _sampler, _constants;

    private IntPtr _hwnd, _factory, _swapChain, _backBufferView;
    private int _bufferWidth, _bufferHeight;
    private bool _visible;
    private double _destX, _destY, _destWidth, _destHeight;

    private readonly Picture _stream = new();
    private readonly Picture _framebuffer = new();
    /// <summary>The stream picture shows (over the framebuffer when <see cref="_streamOffset"/> is set); false: the framebuffer does.</summary>
    private bool _streamShown;
    private (int X, int Y)? _streamOffset;
    private readonly FramebufferTarget _target;
    private bool _disposed;

    public VideoRenderer(D3D11VideoDevice device)
    {
        _target = new FramebufferTarget(this);
        if (!device.AcquireDevice(out _device, out _context)) throw new ObjectDisposedException(nameof(D3D11VideoDevice));
        try { CreatePipeline(); }
        catch { Dispose(); throw; }
    }

    /// <summary>A Present or a resource call failed (device removed or reset); the renderer then refuses frames and the caller should fall back.</summary>
    public string? Failure { get; private set; }
    public bool IsUsable => !_disposed && Failure == null;
    /// <summary>Size of the stream picture last handed in; zero before the first.</summary>
    public int FrameWidth => _stream.Width;
    public int FrameHeight => _stream.Height;
    /// <summary>Size of the framebuffer picture (<see cref="UpdateFramebuffer"/>); zero while there is none.</summary>
    public int FramebufferWidth => _framebuffer.Width;
    public int FramebufferHeight => _framebuffer.Height;
    public long FramesPresented { get; private set; }

    private void CreatePipeline()
    {
        var (vs, ps) = Bytecode.Value;
        fixed (byte* p = vs) MF.Check(D3D11.CreateVertexShader(_device, p, (nuint)vs.Length, out _vs), "CreateVertexShader");
        fixed (byte* p = ps) MF.Check(D3D11.CreatePixelShader(_device, p, (nuint)ps.Length, out _ps), "CreatePixelShader");
        var sampler = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11.FILTER_MIN_MAG_LINEAR_MIP_POINT,
            AddressU = D3D11.TEXTURE_ADDRESS_CLAMP, AddressV = D3D11.TEXTURE_ADDRESS_CLAMP, AddressW = D3D11.TEXTURE_ADDRESS_CLAMP,
            MaxAnisotropy = 1, ComparisonFunc = D3D11.COMPARISON_NEVER, MinLod = 0, MaxLod = float.MaxValue,
        };
        MF.Check(D3D11.CreateSamplerState(_device, &sampler, out _sampler), "CreateSamplerState");
        var cb = new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(Params), Usage = D3D11.USAGE_DEFAULT, BindFlags = D3D11.BIND_CONSTANT_BUFFER };
        MF.Check(D3D11.CreateBuffer(_device, &cb, out _constants), "CreateBuffer");
    }

    // ------------------------------------------------------------------ window and layout

    /// <summary>The window to present into (zero detaches; the swap chain is released before the window goes away).</summary>
    public void SetWindow(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_disposed || hwnd == _hwnd) return;
            ReleaseSwapChain();
            _hwnd = hwnd;
            _bufferWidth = _bufferHeight = 0;
            _visible = false;
        }
    }

    /// <summary>
    /// The window's client size in pixels and where in it the picture goes (pixels; may reach outside the window, which
    /// then crops it). A zero size means the window is hidden: frames are still taken but not presented. Redraws the
    /// last picture when the layout changed. Call from the window's thread, after moving or resizing the window.
    /// </summary>
    public void SetLayout(int width, int height, double destX, double destY, double destWidth, double destHeight)
    {
        lock (_lock)
        {
            if (!IsUsable || _hwnd == IntPtr.Zero) return;
            bool visible = width > 0 && height > 0;
            bool same = visible == _visible && (!visible || (width == _bufferWidth && height == _bufferHeight &&
                destX == _destX && destY == _destY && destWidth == _destWidth && destHeight == _destHeight));
            if (same) return;
            _visible = visible;
            _destX = destX; _destY = destY; _destWidth = destWidth; _destHeight = destHeight;
            if (!visible) return;
            try
            {
                if (_swapChain == IntPtr.Zero) CreateSwapChain(width, height);
                else if (width != _bufferWidth || height != _bufferHeight) ResizeSwapChain(width, height);
                PresentLocked();
            }
            catch (MediaException ex) { Fail(ex.Message); }
        }
    }

    private void CreateSwapChain(int width, int height)
    {
        if (_factory == IntPtr.Zero)
        {
            var dxgi = Com.QueryInterface(_device, Dxgi.IID_IDXGIDevice);
            if (dxgi == IntPtr.Zero) throw new MediaException("query IDXGIDevice", MF.E_FAIL);
            try
            {
                MF.Check(Dxgi.GetAdapter(dxgi, out var adapter), "IDXGIDevice::GetAdapter");
                try { MF.Check(Dxgi.GetParent(adapter, Dxgi.IID_IDXGIFactory2, out _factory), "IDXGIAdapter::GetParent(IDXGIFactory2)"); }
                finally { Com.Release(ref adapter); }
            }
            finally { Com.Release(ref dxgi); }
        }
        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)width, Height = (uint)height, Format = D3D11.FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            BufferUsage = Dxgi.USAGE_RENDER_TARGET_OUTPUT, BufferCount = 2, Scaling = Dxgi.SCALING_STRETCH,
            SwapEffect = Dxgi.SWAP_EFFECT_FLIP_DISCARD, AlphaMode = Dxgi.ALPHA_MODE_IGNORE,
        };
        int hr = Dxgi.CreateSwapChainForHwnd(_factory, _device, _hwnd, &desc, out _swapChain);
        if (hr < 0)
        {
            desc.SwapEffect = Dxgi.SWAP_EFFECT_FLIP_SEQUENTIAL; // Windows 8.1 and older drivers
            hr = Dxgi.CreateSwapChainForHwnd(_factory, _device, _hwnd, &desc, out _swapChain);
        }
        MF.Check(hr, "CreateSwapChainForHwnd");
        // Alt+Enter belongs to the remote machine, and the window is not ours to make fullscreen.
        Dxgi.MakeWindowAssociation(_factory, _hwnd, Dxgi.MWA_NO_ALT_ENTER | Dxgi.MWA_NO_WINDOW_CHANGES);
        _bufferWidth = width;
        _bufferHeight = height;
        CreateBackBufferView();
    }

    private void ResizeSwapChain(int width, int height)
    {
        // Every reference to the back buffers must go before ResizeBuffers, including the context's binding.
        D3D11.ClearState(_context);
        Com.Release(ref _backBufferView);
        D3D11.Flush(_context);
        MF.Check(Dxgi.ResizeBuffers(_swapChain, 0, (uint)width, (uint)height, 0, 0), "ResizeBuffers");
        _bufferWidth = width;
        _bufferHeight = height;
        CreateBackBufferView();
    }

    private void CreateBackBufferView()
    {
        MF.Check(Dxgi.GetBuffer(_swapChain, 0, D3D11.IID_ID3D11Texture2D, out var buffer), "IDXGISwapChain::GetBuffer");
        try { MF.Check(D3D11.CreateRenderTargetView(_device, buffer, out _backBufferView), "CreateRenderTargetView"); }
        finally { Com.Release(ref buffer); }
    }

    private void ReleaseSwapChain()
    {
        if (_context != IntPtr.Zero && _swapChain != IntPtr.Zero)
        {
            D3D11.ClearState(_context);
            D3D11.Flush(_context);
        }
        Com.Release(ref _backBufferView);
        Com.Release(ref _swapChain);
    }

    // ------------------------------------------------------------------ stream frames

    /// <summary>
    /// Takes a decoded picture (a decoder texture or system-memory NV12) as the stream picture and presents it at once
    /// when the window is visible. <paramref name="offset"/>: the picture shows only the part of the desktop whose
    /// top-left corner is at that framebuffer position, and is drawn over the framebuffer there (scaled like it,
    /// clipped to it); null: it is the whole picture. False when the renderer cannot take it (failed, disposed,
    /// unsupported texture format); the caller then shows the frame another way. Call inside the decoder's frame callback.
    /// </summary>
    public bool Present(in DecodedFrame frame, (int X, int Y)? offset = null)
    {
        lock (_lock)
        {
            if (!IsUsable || frame.Width <= 0 || frame.Height <= 0) return false;
            try
            {
                if (frame.IsTexture)
                {
                    if (!CopyTexture(in frame)) return false;
                }
                else UploadPlanes(in frame);
                ShowStreamLocked(offset);
                PresentLocked();
                return IsUsable;
            }
            catch (MediaException ex) { Fail(ex.Message); return false; }
        }
    }

    /// <summary>Takes a BGRX picture (width*4 bytes per row) as the stream picture and presents it like <see cref="Present(in DecodedFrame, ValueTuple{int, int}?)"/>.</summary>
    public bool Present(ReadOnlySpan<byte> bgrx, int width, int height, (int X, int Y)? offset = null)
    {
        lock (_lock)
        {
            if (!IsUsable || width <= 0 || height <= 0 || bgrx.Length < width * height * 4) return false;
            try
            {
                EnsureTextures(_stream, Source.Bgrx, width, height);
                fixed (byte* p = bgrx) D3D11.UpdateSubresource(_context, _stream.Texture0, 0, p, (uint)(width * 4), 0);
                SetPicture(_stream, 0, 0, width, height);
                ShowStreamLocked(offset);
                PresentLocked();
                return IsUsable;
            }
            catch (MediaException ex) { Fail(ex.Message); return false; }
        }
    }

    private void ShowStreamLocked((int X, int Y)? offset)
    {
        _streamShown = true;
        _streamOffset = offset;
    }

    /// <summary>
    /// Takes the stream picture off the screen: the framebuffer shows again (black without one). The picture stays, so
    /// a later frame needs no new textures. Any thread.
    /// </summary>
    public void HideStream()
    {
        lock (_lock)
        {
            if (!IsUsable || !_streamShown) return;
            _streamShown = false;
            _streamOffset = null;
            try { PresentLocked(); }
            catch (MediaException ex) { Fail(ex.Message); }
        }
    }

    /// <summary>True while the stream picture shows (over part of the framebuffer, or instead of it).</summary>
    public bool IsStreamShown
    {
        get { lock (_lock) return _streamShown; }
    }

    private bool CopyTexture(in DecodedFrame frame)
    {
        D3D11.GetDesc(frame.Texture, out var desc);
        if (desc.Format != D3D11.FORMAT_NV12) return false;
        EnsureTextures(_stream, Source.Nv12Texture, (int)desc.Width, (int)desc.Height);
        // The whole slice: NV12 is one subresource in D3D11, both planes travel together.
        D3D11.CopySubresourceRegion(_context, _stream.Texture0, 0, 0, 0, 0, frame.Texture, frame.Subresource, null);
        SetPicture(_stream, frame.OffsetX, frame.OffsetY, frame.Width, frame.Height);
        return true;
    }

    private void UploadPlanes(in DecodedFrame frame)
    {
        EnsureTextures(_stream, Source.Nv12Planes, frame.Width, frame.Height);
        D3D11.UpdateSubresource(_context, _stream.Texture0, 0, (void*)frame.Y, (uint)frame.Stride, 0);
        D3D11.UpdateSubresource(_context, _stream.Texture1, 0, (void*)frame.UV, (uint)frame.Stride, 0);
        SetPicture(_stream, 0, 0, frame.Width, frame.Height);
    }

    private static void SetPicture(Picture picture, int x, int y, int width, int height)
    {
        picture.X = x; picture.Y = y; picture.Width = width; picture.Height = height;
    }

    // ------------------------------------------------------------------ framebuffer

    /// <summary>Copies changed parts of the remote framebuffer into the renderer; see <see cref="UpdateFramebuffer"/>.</summary>
    public delegate void FramebufferCopy(FramebufferTarget target);

    /// <summary>
    /// The framebuffer picture while a <see cref="FramebufferCopy"/> runs: size it, then copy the changed rectangles in.
    /// Valid only inside that callback.
    /// </summary>
    public sealed class FramebufferTarget
    {
        private readonly VideoRenderer _owner;
        internal bool Active;

        internal FramebufferTarget(VideoRenderer owner) => _owner = owner;

        /// <summary>
        /// Makes the framebuffer picture <paramref name="width"/>×<paramref name="height"/> BGRX. True when it was
        /// created or resized: its content is then undefined, so copy all of it.
        /// </summary>
        public bool EnsureSize(int width, int height)
        {
            if (!Active) throw new InvalidOperationException("Only inside UpdateFramebuffer.");
            return _owner.EnsureFramebufferLocked(width, height);
        }

        /// <summary>
        /// Uploads the rectangle at <paramref name="x"/>,<paramref name="y"/>: <paramref name="source"/> points at its
        /// first pixel, <paramref name="rowPitch"/> bytes per source row. Clipped to the picture.
        /// </summary>
        public void Copy(IntPtr source, int rowPitch, int x, int y, int width, int height)
        {
            if (!Active) throw new InvalidOperationException("Only inside UpdateFramebuffer.");
            _owner.CopyFramebufferLocked(source, rowPitch, x, y, width, height);
        }
    }

    /// <summary>
    /// Updates the framebuffer picture: <paramref name="copy"/> runs under the renderer's lock, sizes the picture and
    /// uploads the rectangles that changed (only those reach the GPU). Then presents, when the framebuffer is what shows
    /// (no stream, or a stream of part of the desktop), and returns whether it did. False also when the renderer failed;
    /// check <see cref="IsUsable"/>. Any thread; presenting blocks about a frame, so not the UI thread.
    /// </summary>
    public bool UpdateFramebuffer(FramebufferCopy copy)
    {
        lock (_lock)
        {
            if (!IsUsable) return false;
            try
            {
                _target.Active = true;
                try { copy(_target); }
                finally { _target.Active = false; }
                if (!IsUsable || !FramebufferShowsLocked() || !_visible || _swapChain == IntPtr.Zero) return false;
                PresentLocked();
                return IsUsable;
            }
            catch (MediaException ex) { Fail(ex.Message); return false; }
        }
    }

    /// <summary>Drops the framebuffer picture (a session without RFB, or none at all); the stream alone shows. Any thread.</summary>
    public void ClearFramebuffer()
    {
        lock (_lock)
        {
            if (_disposed || _framebuffer.Kind == Source.None) return;
            ReleaseTextures(_framebuffer);
            if (!IsUsable) return;
            try { PresentLocked(); }
            catch (MediaException ex) { Fail(ex.Message); }
        }
    }

    private bool FramebufferShowsLocked() =>
        _framebuffer.HasContent && (!_streamShown || !_stream.HasContent || _streamOffset != null);

    private bool EnsureFramebufferLocked(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            ReleaseTextures(_framebuffer);
            return false;
        }
        bool fresh = _framebuffer.Kind != Source.Bgrx || _framebuffer.TextureWidth != width || _framebuffer.TextureHeight != height;
        EnsureTextures(_framebuffer, Source.Bgrx, width, height);
        SetPicture(_framebuffer, 0, 0, width, height);
        return fresh;
    }

    private void CopyFramebufferLocked(IntPtr source, int rowPitch, int x, int y, int width, int height)
    {
        if (_framebuffer.Kind != Source.Bgrx || source == IntPtr.Zero) return;
        int left = Math.Max(0, x), top = Math.Max(0, y);
        int right = Math.Min(_framebuffer.Width, x + width), bottom = Math.Min(_framebuffer.Height, y + height);
        if (right <= left || bottom <= top) return;
        var box = new D3D11_BOX { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
        byte* first = (byte*)source + (long)(top - y) * rowPitch + (left - x) * 4;
        D3D11.UpdateSubresource(_context, _framebuffer.Texture0, 0, &box, first, (uint)rowPitch, 0);
    }

    // ------------------------------------------------------------------ textures

    /// <summary>(Re)creates a picture's textures and their views when its source kind or size changes.</summary>
    private void EnsureTextures(Picture picture, Source source, int width, int height)
    {
        if (source == picture.Kind && width == picture.TextureWidth && height == picture.TextureHeight) return;
        ReleaseTextures(picture);
        switch (source)
        {
            case Source.Nv12Texture:
                picture.Texture0 = CreateTexture(D3D11.FORMAT_NV12, width, height);
                picture.View0 = CreateView(picture.Texture0, D3D11.FORMAT_R8_UNORM);   // the luma plane
                picture.View1 = CreateView(picture.Texture0, D3D11.FORMAT_R8G8_UNORM); // the chroma plane
                break;
            case Source.Nv12Planes:
                picture.Texture0 = CreateTexture(D3D11.FORMAT_R8_UNORM, width, height);
                picture.Texture1 = CreateTexture(D3D11.FORMAT_R8G8_UNORM, (width + 1) / 2, (height + 1) / 2);
                picture.View0 = CreateView(picture.Texture0, D3D11.FORMAT_R8_UNORM);
                picture.View1 = CreateView(picture.Texture1, D3D11.FORMAT_R8G8_UNORM);
                break;
            case Source.Bgrx:
                picture.Texture0 = CreateTexture(D3D11.FORMAT_B8G8R8A8_UNORM, width, height);
                picture.View0 = CreateView(picture.Texture0, D3D11.FORMAT_B8G8R8A8_UNORM);
                break;
        }
        picture.Kind = source;
        picture.TextureWidth = width;
        picture.TextureHeight = height;
    }

    private IntPtr CreateTexture(int format, int width, int height, uint bind = D3D11.BIND_SHADER_RESOURCE, int usage = D3D11.USAGE_DEFAULT, uint cpu = 0)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleCount = 1, Usage = usage, BindFlags = bind, CpuAccessFlags = cpu,
        };
        MF.Check(D3D11.CreateTexture2D(_device, &desc, out var texture), $"CreateTexture2D {width}x{height} format {format}");
        return texture;
    }

    private IntPtr CreateView(IntPtr texture, int format)
    {
        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = format, ViewDimension = D3D11.SRV_DIMENSION_TEXTURE2D, MipLevels = 1 };
        MF.Check(D3D11.CreateShaderResourceView(_device, texture, &desc, out var view), $"CreateShaderResourceView format {format}");
        return view;
    }

    private static void ReleaseTextures(Picture picture)
    {
        Com.Release(ref picture.View0);
        Com.Release(ref picture.View1);
        Com.Release(ref picture.Texture0);
        Com.Release(ref picture.Texture1);
        picture.Kind = Source.None;
        picture.TextureWidth = picture.TextureHeight = 0;
        picture.X = picture.Y = picture.Width = picture.Height = 0;
    }

    // ------------------------------------------------------------------ drawing

    private void PresentLocked()
    {
        if (!_visible || _swapChain == IntPtr.Zero) return;
        if (!_stream.HasContent && !_framebuffer.HasContent) return;
        Draw(_backBufferView, _bufferWidth, _bufferHeight, _destX, _destY, _destWidth, _destHeight);
        int hr = Dxgi.Present(_swapChain, 0, 0);
        if (hr < 0)
        {
            int reason = D3D11.GetDeviceRemovedReason(_device);
            Fail($"Present failed (0x{hr:X8}, device 0x{reason:X8})");
            return;
        }
        FramesPresented++;
    }

    /// <summary>
    /// Clears <paramref name="target"/> (<paramref name="targetWidth"/>×<paramref name="targetHeight"/>) to black and
    /// draws what shows into the destination rectangle: the framebuffer, the stream, or the stream over part of the framebuffer.
    /// </summary>
    private void Draw(IntPtr target, int targetWidth, int targetHeight, double x, double y, double width, double height)
    {
        float* black = stackalloc float[] { 0, 0, 0, 1 };
        IntPtr rtv = target;
        D3D11.OMSetRenderTargets(_context, 1, &rtv);
        D3D11.ClearRenderTargetView(_context, target, black);
        if (width <= 0 || height <= 0) return;
        var clip = new PixelRect(0, 0, targetWidth, targetHeight);
        if (FramebufferShowsLocked()) DrawPicture(_framebuffer, new PixelRect(x, y, width, height), clip);
        if (!_streamShown || !_stream.HasContent) return;
        if (_streamOffset is { } at && _framebuffer.HasContent)
        {
            // Scaled like the framebuffer, onto the part of it the stream shows, and no further than the framebuffer.
            double sx = width / _framebuffer.Width, sy = height / _framebuffer.Height;
            var part = new PixelRect(x + at.X * sx, y + at.Y * sy, _stream.Width * sx, _stream.Height * sy);
            DrawPicture(_stream, part, clip.Intersect(new PixelRect(x, y, width, height)));
        }
        else DrawPicture(_stream, new PixelRect(x, y, width, height), clip);
    }

    /// <summary>Draws <paramref name="picture"/> scaled into <paramref name="dest"/>, only where it lies inside <paramref name="clip"/>.</summary>
    private void DrawPicture(Picture picture, PixelRect dest, PixelRect clip)
    {
        var shown = dest.Intersect(clip);
        if (shown.IsEmpty || !picture.HasContent) return;
        double tw = picture.TextureWidth, th = picture.TextureHeight;
        // The part of the picture that lands in the visible rectangle, in texels.
        double u0 = picture.X + (shown.X - dest.X) / dest.Width * picture.Width;
        double v0 = picture.Y + (shown.Y - dest.Y) / dest.Height * picture.Height;
        double uw = shown.Width / dest.Width * picture.Width, vh = shown.Height / dest.Height * picture.Height;
        // Texels per destination pixel: when shrinking, four taps a quarter pixel apart keep text from shimmering.
        double sx = picture.Width / dest.Width, sy = picture.Height / dest.Height;
        bool taps = sx > 1.25 || sy > 1.25;
        var p = new Params
        {
            OriginX = (float)(u0 / tw), OriginY = (float)(v0 / th),
            SizeX = (float)(uw / tw), SizeY = (float)(vh / th),
            MinX = (float)((picture.X + 0.5) / tw), MinY = (float)((picture.Y + 0.5) / th),
            MaxX = (float)((picture.X + picture.Width - 0.5) / tw), MaxY = (float)((picture.Y + picture.Height - 0.5) / th),
            TapX = taps ? (float)(0.25 * sx / tw) : 0,
            TapY = taps ? (float)(0.25 * sy / th) : 0,
            Rgb = picture.Kind == Source.Bgrx ? 1u : 0u,
        };
        D3D11.UpdateSubresource(_context, _constants, 0, &p, 0, 0);

        var viewport = new D3D11_VIEWPORT
        {
            TopLeftX = (float)shown.X, TopLeftY = (float)shown.Y,
            Width = (float)Math.Min(shown.Width, 16384), Height = (float)Math.Min(shown.Height, 16384), MaxDepth = 1,
        };
        D3D11.RSSetViewports(_context, 1, &viewport);
        D3D11.IASetInputLayout(_context, IntPtr.Zero);
        D3D11.IASetPrimitiveTopology(_context, D3D11.PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        D3D11.VSSetShader(_context, _vs);
        D3D11.PSSetShader(_context, _ps);
        IntPtr constants = _constants, sampler = _sampler;
        D3D11.VSSetConstantBuffers(_context, 0, 1, &constants);
        D3D11.PSSetConstantBuffers(_context, 0, 1, &constants);
        D3D11.PSSetSamplers(_context, 0, 1, &sampler);
        IntPtr* views = stackalloc IntPtr[3];
        if (picture.Kind == Source.Bgrx) { views[0] = views[1] = IntPtr.Zero; views[2] = picture.View0; }
        else { views[0] = picture.View0; views[1] = picture.View1; views[2] = IntPtr.Zero; }
        D3D11.PSSetShaderResources(_context, 0, 3, views);
        D3D11.Draw(_context, 4, 0);
        // Unbind the picture so the next copy or upload into it never meets it bound as a shader input.
        views[0] = views[1] = views[2] = IntPtr.Zero;
        D3D11.PSSetShaderResources(_context, 0, 3, views);
    }

    /// <summary>
    /// Renders what shows at its own size into a BGRA buffer (width*4 bytes per row, alpha 255): the screenshot. That is
    /// the stream picture while a whole-desktop stream shows, otherwise the framebuffer (with a partial stream drawn over
    /// it). Null when there is no picture. Any thread.
    /// </summary>
    public byte[]? CaptureFrame(out int width, out int height)
    {
        lock (_lock)
        {
            var size = FramebufferShowsLocked() ? _framebuffer : _streamShown || !_framebuffer.HasContent ? _stream : _framebuffer;
            width = size.Width;
            height = size.Height;
            if (!IsUsable || !size.HasContent || width <= 0 || height <= 0) return null;
            IntPtr target = IntPtr.Zero, view = IntPtr.Zero, staging = IntPtr.Zero;
            bool streamShown = _streamShown;
            try
            {
                // A stream never shown (a test, or a caller without HideStream) still captures as before.
                if (ReferenceEquals(size, _stream)) _streamShown = true;
                target = CreateTexture(D3D11.FORMAT_B8G8R8A8_UNORM, width, height, D3D11.BIND_RENDER_TARGET);
                MF.Check(D3D11.CreateRenderTargetView(_device, target, out view), "CreateRenderTargetView");
                Draw(view, width, height, 0, 0, width, height);
                staging = CreateTexture(D3D11.FORMAT_B8G8R8A8_UNORM, width, height, 0, D3D11.USAGE_STAGING, D3D11.CPU_ACCESS_READ);
                D3D11.CopyResource(_context, staging, target);
                MF.Check(D3D11.Map(_context, staging, 0, D3D11.MAP_READ, out var mapped), "Map");
                var pixels = new byte[width * height * 4];
                try
                {
                    fixed (byte* d = pixels)
                        for (int row = 0; row < height; row++)
                            Buffer.MemoryCopy((byte*)mapped.Data + (long)row * mapped.RowPitch, d + (long)row * width * 4, width * 4, width * 4);
                }
                finally { D3D11.Unmap(_context, staging, 0); }
                return pixels;
            }
            catch (MediaException) { return null; }
            finally
            {
                _streamShown = streamShown;
                D3D11.ClearState(_context);
                Com.Release(ref view);
                Com.Release(ref target);
                Com.Release(ref staging);
            }
        }
    }

    private void Fail(string reason)
    {
        Failure ??= reason;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSwapChain();
            ReleaseTextures(_stream);
            ReleaseTextures(_framebuffer);
            Com.Release(ref _factory);
            Com.Release(ref _constants);
            Com.Release(ref _sampler);
            Com.Release(ref _ps);
            Com.Release(ref _vs);
            if (_context != IntPtr.Zero) D3D11.ClearState(_context);
            Com.Release(ref _context);
            Com.Release(ref _device);
        }
    }
}
