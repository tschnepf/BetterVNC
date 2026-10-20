using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>
/// Shows streamed pictures from the GPU: a flip-model swap chain on a window, a pixel shader that converts NV12 to RGB
/// (BT.601 limited range, the same matrix as <see cref="PixelConvert.Nv12ToBgrx"/>) and scales the visible picture
/// into a destination rectangle, and Present(0) at once for every frame handed in, so the newest frame is what the
/// compositor shows next (flip-discard keeps no queue; the device's frame latency is one).
/// <para>
/// Hardware-decoded frames are copied GPU-to-GPU out of the decoder's surface pool into the renderer's own NV12
/// texture (the decoder gets its surface back at once and the picture can be redrawn after a resize); system-memory
/// NV12 (software decode) and BGRX (motion JPEG) frames are uploaded as they are and converted by the same shader.
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

    private readonly object _lock = new();
    private IntPtr _device, _context;
    private IntPtr _vs, _ps, _sampler, _constants;

    private IntPtr _hwnd, _factory, _swapChain, _backBufferView;
    private int _bufferWidth, _bufferHeight;
    private bool _visible;
    private double _destX, _destY, _destWidth, _destHeight;

    private Source _source;
    private IntPtr _texture0, _texture1, _view0, _view1;
    private int _textureWidth, _textureHeight;
    private int _pictureX, _pictureY, _pictureWidth, _pictureHeight;
    private bool _disposed;

    public VideoRenderer(D3D11VideoDevice device)
    {
        if (!device.AcquireDevice(out _device, out _context)) throw new ObjectDisposedException(nameof(D3D11VideoDevice));
        try { CreatePipeline(); }
        catch { Dispose(); throw; }
    }

    /// <summary>A Present or a resource call failed (device removed or reset); the renderer then refuses frames and the caller should fall back.</summary>
    public string? Failure { get; private set; }
    public bool IsUsable => !_disposed && Failure == null;
    /// <summary>Size of the picture last handed in; zero before the first.</summary>
    public int FrameWidth => _pictureWidth;
    public int FrameHeight => _pictureHeight;
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

    // ------------------------------------------------------------------ frames

    /// <summary>
    /// Takes a decoded picture (a decoder texture or system-memory NV12) and presents it at once when the window is
    /// visible. False when the renderer cannot take it (failed, disposed, unsupported texture format); the caller then
    /// shows the frame another way. Call inside the decoder's frame callback.
    /// </summary>
    public bool Present(in DecodedFrame frame)
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
                PresentLocked();
                return IsUsable;
            }
            catch (MediaException ex) { Fail(ex.Message); return false; }
        }
    }

    /// <summary>Takes a BGRX picture (width*4 bytes per row) and presents it like <see cref="Present(in DecodedFrame)"/>.</summary>
    public bool Present(ReadOnlySpan<byte> bgrx, int width, int height)
    {
        lock (_lock)
        {
            if (!IsUsable || width <= 0 || height <= 0 || bgrx.Length < width * height * 4) return false;
            try
            {
                EnsureTextures(Source.Bgrx, width, height);
                fixed (byte* p = bgrx) D3D11.UpdateSubresource(_context, _texture0, 0, p, (uint)(width * 4), 0);
                SetPicture(0, 0, width, height);
                PresentLocked();
                return IsUsable;
            }
            catch (MediaException ex) { Fail(ex.Message); return false; }
        }
    }

    private bool CopyTexture(in DecodedFrame frame)
    {
        D3D11.GetDesc(frame.Texture, out var desc);
        if (desc.Format != D3D11.FORMAT_NV12) return false;
        EnsureTextures(Source.Nv12Texture, (int)desc.Width, (int)desc.Height);
        // The whole slice: NV12 is one subresource in D3D11, both planes travel together.
        D3D11.CopySubresourceRegion(_context, _texture0, 0, 0, 0, 0, frame.Texture, frame.Subresource, null);
        SetPicture(frame.OffsetX, frame.OffsetY, frame.Width, frame.Height);
        return true;
    }

    private void UploadPlanes(in DecodedFrame frame)
    {
        EnsureTextures(Source.Nv12Planes, frame.Width, frame.Height);
        D3D11.UpdateSubresource(_context, _texture0, 0, (void*)frame.Y, (uint)frame.Stride, 0);
        D3D11.UpdateSubresource(_context, _texture1, 0, (void*)frame.UV, (uint)frame.Stride, 0);
        SetPicture(0, 0, frame.Width, frame.Height);
    }

    private void SetPicture(int x, int y, int width, int height)
    {
        _pictureX = x; _pictureY = y; _pictureWidth = width; _pictureHeight = height;
    }

    /// <summary>(Re)creates the picture textures and their views when the source kind or size changes.</summary>
    private void EnsureTextures(Source source, int width, int height)
    {
        if (source == _source && width == _textureWidth && height == _textureHeight) return;
        ReleaseTextures();
        switch (source)
        {
            case Source.Nv12Texture:
                _texture0 = CreateTexture(D3D11.FORMAT_NV12, width, height);
                _view0 = CreateView(_texture0, D3D11.FORMAT_R8_UNORM);   // the luma plane
                _view1 = CreateView(_texture0, D3D11.FORMAT_R8G8_UNORM); // the chroma plane
                break;
            case Source.Nv12Planes:
                _texture0 = CreateTexture(D3D11.FORMAT_R8_UNORM, width, height);
                _texture1 = CreateTexture(D3D11.FORMAT_R8G8_UNORM, (width + 1) / 2, (height + 1) / 2);
                _view0 = CreateView(_texture0, D3D11.FORMAT_R8_UNORM);
                _view1 = CreateView(_texture1, D3D11.FORMAT_R8G8_UNORM);
                break;
            case Source.Bgrx:
                _texture0 = CreateTexture(D3D11.FORMAT_B8G8R8A8_UNORM, width, height);
                _view0 = CreateView(_texture0, D3D11.FORMAT_B8G8R8A8_UNORM);
                break;
        }
        _source = source;
        _textureWidth = width;
        _textureHeight = height;
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

    private void ReleaseTextures()
    {
        Com.Release(ref _view0);
        Com.Release(ref _view1);
        Com.Release(ref _texture0);
        Com.Release(ref _texture1);
        _source = Source.None;
        _textureWidth = _textureHeight = 0;
    }

    // ------------------------------------------------------------------ drawing

    private void PresentLocked()
    {
        if (!_visible || _swapChain == IntPtr.Zero || _source == Source.None) return;
        Draw(_backBufferView, _destX, _destY, _destWidth, _destHeight);
        int hr = Dxgi.Present(_swapChain, 0, 0);
        if (hr < 0)
        {
            int reason = D3D11.GetDeviceRemovedReason(_device);
            Fail($"Present failed (0x{hr:X8}, device 0x{reason:X8})");
            return;
        }
        FramesPresented++;
    }

    /// <summary>Clears <paramref name="target"/> to black and draws the current picture into the destination rectangle.</summary>
    private void Draw(IntPtr target, double x, double y, double width, double height)
    {
        float* black = stackalloc float[] { 0, 0, 0, 1 };
        IntPtr rtv = target;
        D3D11.OMSetRenderTargets(_context, 1, &rtv);
        D3D11.ClearRenderTargetView(_context, target, black);
        if (width <= 0 || height <= 0 || _source == Source.None) return;

        double tw = _textureWidth, th = _textureHeight;
        // Texels per destination pixel: when shrinking, four taps a quarter pixel apart keep text from shimmering.
        double sx = _pictureWidth / width, sy = _pictureHeight / height;
        var p = new Params
        {
            OriginX = (float)(_pictureX / tw), OriginY = (float)(_pictureY / th),
            SizeX = (float)(_pictureWidth / tw), SizeY = (float)(_pictureHeight / th),
            MinX = (float)((_pictureX + 0.5) / tw), MinY = (float)((_pictureY + 0.5) / th),
            MaxX = (float)((_pictureX + _pictureWidth - 0.5) / tw), MaxY = (float)((_pictureY + _pictureHeight - 0.5) / th),
            TapX = sx > 1.25 || sy > 1.25 ? (float)(0.25 * sx / tw) : 0,
            TapY = sx > 1.25 || sy > 1.25 ? (float)(0.25 * sy / th) : 0,
            Rgb = _source == Source.Bgrx ? 1u : 0u,
        };
        D3D11.UpdateSubresource(_context, _constants, 0, &p, 0, 0);

        var viewport = new D3D11_VIEWPORT
        {
            TopLeftX = (float)Math.Clamp(x, -16384, 16383), TopLeftY = (float)Math.Clamp(y, -16384, 16383),
            Width = (float)Math.Min(width, 16384), Height = (float)Math.Min(height, 16384), MaxDepth = 1,
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
        if (_source == Source.Bgrx) { views[0] = views[1] = IntPtr.Zero; views[2] = _view0; }
        else { views[0] = _view0; views[1] = _view1; views[2] = IntPtr.Zero; }
        D3D11.PSSetShaderResources(_context, 0, 3, views);
        D3D11.Draw(_context, 4, 0);
        // Unbind the picture so the next copy or upload into it never meets it bound as a shader input.
        views[0] = views[1] = views[2] = IntPtr.Zero;
        D3D11.PSSetShaderResources(_context, 0, 3, views);
    }

    /// <summary>
    /// Renders the current picture at its own size into a BGRA buffer (width*4 bytes per row, alpha 255): the
    /// screenshot of what the stream shows. Null when there is no picture. Any thread.
    /// </summary>
    public byte[]? CaptureFrame(out int width, out int height)
    {
        lock (_lock)
        {
            width = _pictureWidth;
            height = _pictureHeight;
            if (!IsUsable || _source == Source.None || width <= 0 || height <= 0) return null;
            IntPtr target = IntPtr.Zero, view = IntPtr.Zero, staging = IntPtr.Zero;
            try
            {
                target = CreateTexture(D3D11.FORMAT_B8G8R8A8_UNORM, width, height, D3D11.BIND_RENDER_TARGET);
                MF.Check(D3D11.CreateRenderTargetView(_device, target, out view), "CreateRenderTargetView");
                Draw(view, 0, 0, width, height);
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
            ReleaseTextures();
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
