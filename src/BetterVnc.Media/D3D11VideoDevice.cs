using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>
/// The Direct3D 11 device a hardware H.264 decoder and the <see cref="VideoRenderer"/> share, so decoded pictures stay
/// on the GPU from decode to screen. Set up the way Jump Desktop sets up its viewer's device: video support,
/// multithread protection (the decoder works on it from the decode thread while the renderer resizes from the UI
/// thread), the highest GPU thread priority the system grants and a maximum frame latency of one. Also carries the
/// Media Foundation device manager that hands the device to a decoder (MFT_MESSAGE_SET_D3D_MANAGER).
/// </summary>
public sealed unsafe class D3D11VideoDevice : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _device, _context, _manager;
    private bool _disposed;

    private D3D11VideoDevice(IntPtr device, IntPtr context)
    {
        _device = device;
        _context = context;
    }

    /// <summary>The GPU, as DXGI names it.</summary>
    public string AdapterName { get; private set; } = "";
    /// <summary>The device was created with video support, so a decoder can use DXVA on it.</summary>
    public bool SupportsVideo { get; private set; }
    /// <summary>NV12 textures can be sampled in a shader (needed to show hardware-decoded pictures without a copy through the CPU).</summary>
    public bool SupportsNv12Textures { get; private set; }
    /// <summary>The GPU thread priority that was granted (Jump asks for 7, the maximum; the system may refuse).</summary>
    public int GpuThreadPriority { get; private set; }

    internal IntPtr Device => _device;
    internal IntPtr Context => _context;

    /// <summary>Creates a hardware device; throws <see cref="MediaException"/> when there is no usable GPU (none, or only the Basic Render Driver).</summary>
    public static D3D11VideoDevice Create()
    {
        MediaRuntime.EnsureStarted();
        int* levels = stackalloc int[] { D3D11.FEATURE_LEVEL_11_1, D3D11.FEATURE_LEVEL_11_0, D3D11.FEATURE_LEVEL_10_1, D3D11.FEATURE_LEVEL_10_0 };
        bool video = true;
        int hr = D3D11.D3D11CreateDevice(IntPtr.Zero, D3D.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11.CREATE_DEVICE_BGRA_SUPPORT | D3D11.CREATE_DEVICE_VIDEO_SUPPORT,
            levels, 4, D3D.D3D11_SDK_VERSION, out var device, out _, out var context);
        if (hr < 0)
        {
            // A driver without video support can still present; decoding then stays in software.
            video = false;
            hr = D3D11.D3D11CreateDevice(IntPtr.Zero, D3D.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11.CREATE_DEVICE_BGRA_SUPPORT,
                levels, 4, D3D.D3D11_SDK_VERSION, out device, out _, out context);
        }
        MF.Check(hr, "D3D11CreateDevice");
        var result = new D3D11VideoDevice(device, context) { SupportsVideo = video };
        try { result.Configure(); }
        catch { result.Dispose(); throw; }
        return result;
    }

    private void Configure()
    {
        var mt = Com.QueryInterface(_device, D3D11.IID_ID3D10Multithread);
        if (mt == IntPtr.Zero) throw new MediaException("query ID3D10Multithread", MF.E_FAIL);
        D3D11.SetMultithreadProtected(mt, true);
        Com.Release(ref mt);

        var dxgi = Com.QueryInterface(_device, Dxgi.IID_IDXGIDevice);
        if (dxgi == IntPtr.Zero) throw new MediaException("query IDXGIDevice", MF.E_FAIL);
        try
        {
            for (int priority = 7; priority > 0; priority--)
                if (Dxgi.SetGPUThreadPriority(dxgi, priority) >= 0) { GpuThreadPriority = priority; break; }
            if (Dxgi.GetAdapter(dxgi, out var adapter) >= 0)
            {
                if (Dxgi.GetDesc(adapter, out var desc) >= 0)
                {
                    AdapterName = new string(desc.Description).TrimEnd('\0').Trim();
                    if (desc.VendorId == Dxgi.VENDOR_MICROSOFT && desc.DeviceId == Dxgi.DEVICE_BASIC_RENDER)
                    {
                        Com.Release(ref adapter);
                        throw new MediaException("Direct3D runs on the Basic Render Driver (no GPU)", D3D.DXGI_ERROR_UNSUPPORTED);
                    }
                }
                Com.Release(ref adapter);
            }
        }
        finally { Com.Release(ref dxgi); }

        var dxgi1 = Com.QueryInterface(_device, Dxgi.IID_IDXGIDevice1);
        if (dxgi1 != IntPtr.Zero)
        {
            Dxgi.SetMaximumFrameLatency(dxgi1, 1);
            Com.Release(ref dxgi1);
        }

        uint nv12 = D3D11.CheckFormatSupport(_device, D3D11.FORMAT_NV12);
        SupportsNv12Textures = (nv12 & (D3D11.FORMAT_SUPPORT_TEXTURE2D | D3D11.FORMAT_SUPPORT_SHADER_SAMPLE)) == (D3D11.FORMAT_SUPPORT_TEXTURE2D | D3D11.FORMAT_SUPPORT_SHADER_SAMPLE);

        if (SupportsVideo)
        {
            MF.Check(MF.MFCreateDXGIDeviceManager(out uint token, out _manager), "MFCreateDXGIDeviceManager");
            MF.Check(MF.ResetDevice(_manager, _device, token), "IMFDXGIDeviceManager::ResetDevice");
        }
    }

    /// <summary>
    /// The IMFDXGIDeviceManager for a decoder, with a reference the caller releases (the decoder keeps its own), or
    /// zero when this device cannot decode (no video support, NV12 not sampleable, or disposed). Any thread.
    /// </summary>
    internal IntPtr AcquireManager()
    {
        lock (_lock)
        {
            if (_disposed || _manager == IntPtr.Zero || !SupportsNv12Textures) return IntPtr.Zero;
            Marshal.AddRef(_manager);
            return _manager;
        }
    }

    /// <summary>AddRefs the device and its immediate context for an object that outlives this wrapper's own use of them.</summary>
    internal bool AcquireDevice(out IntPtr device, out IntPtr context)
    {
        lock (_lock)
        {
            device = context = IntPtr.Zero;
            if (_disposed) return false;
            Marshal.AddRef(_device);
            Marshal.AddRef(_context);
            device = _device;
            context = _context;
            return true;
        }
    }

    /// <summary>Releases this wrapper's references; decoders and renderers made from it keep working on their own.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Com.Release(ref _manager);
            Com.Release(ref _context);
            Com.Release(ref _device);
        }
    }
}
