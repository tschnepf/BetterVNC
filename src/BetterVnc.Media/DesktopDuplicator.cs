using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>The duplication was invalidated (desktop switch, mode change, lock screen); create a new one.</summary>
public sealed class DesktopDuplicationLostException : Exception
{
    public DesktopDuplicationLostException(string message) : base(message) { }
}

/// <summary>
/// Screen capture with DXGI Desktop Duplication: the compositor hands over a new frame only when something was
/// drawn, so an unchanged screen costs nothing and "nothing changed" is known for free. Every attached output of
/// the primary adapter is duplicated and the frames are composed into one BGRA image of the virtual screen.
/// Unavailable in RDP sessions, on the secure desktop and on basic display adapters; the caller then falls back
/// to GDI. All methods must be called from one thread.
/// </summary>
public sealed unsafe class DesktopDuplicator : IDisposable
{
    private sealed class Output
    {
        public IDXGIOutputDuplication Duplication = null!;
        public ID3D11Texture2D Staging = null!;
        public int X, Y, Width, Height;
    }

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private readonly List<Output> _outputs = new();
    private bool _primaryOnly;
    private bool _disposed;

    /// <summary>Virtual-screen origin and size covered by the captured outputs.</summary>
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OutputCount => _outputs.Count;

    private DesktopDuplicator() { }

    /// <summary>
    /// Throws a <see cref="MediaException"/> or <see cref="InvalidOperationException"/> with the reason when duplication is
    /// unavailable. With <paramref name="primaryOnly"/> only the display at the desktop origin is captured (for desktops
    /// too large for a video encoder).
    /// </summary>
    public static DesktopDuplicator Create(bool primaryOnly = false)
    {
        var d = new DesktopDuplicator { _primaryOnly = primaryOnly };
        try { d.Initialize(); }
        catch { d.Dispose(); throw; }
        return d;
    }

    private void Initialize()
    {
        MF.Check(D3D.D3D11CreateDevice(IntPtr.Zero, D3D.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D.D3D11_SDK_VERSION,
            out _device, out _, out _context), "D3D11CreateDevice");
        var dxgiDevice = (IDXGIDevice)_device;
        MF.Check(dxgiDevice.GetAdapter(out var adapter), "IDXGIDevice.GetAdapter");
        try
        {
            var descs = new List<(IDXGIOutput Output, RECT Rect)>();
            for (uint i = 0; ; i++)
            {
                int hr = adapter.EnumOutputs(i, out var output);
                if (hr == D3D.DXGI_ERROR_NOT_FOUND || output == null) break;
                MF.Check(hr, "EnumOutputs");
                MF.Check(output.GetDesc(out var desc), "IDXGIOutput.GetDesc");
                if (desc.AttachedToDesktop == 0) { MF.Release(output); continue; }
                descs.Add((output, desc.DesktopCoordinates));
            }
            if (descs.Count == 0) throw new InvalidOperationException("No display is attached to the desktop.");
            if (_primaryOnly && descs.Count > 1)
            {
                var primary = descs.FirstOrDefault(d => d.Rect.Left == 0 && d.Rect.Top == 0);
                if (primary.Output == null) primary = descs[0];
                foreach (var d in descs) if (d.Output != primary.Output) MF.Release(d.Output);
                descs = new List<(IDXGIOutput Output, RECT Rect)> { primary };
            }
            Left = descs.Min(d => d.Rect.Left);
            Top = descs.Min(d => d.Rect.Top);
            Width = descs.Max(d => d.Rect.Right) - Left;
            Height = descs.Max(d => d.Rect.Bottom) - Top;

            foreach (var (output, rect) in descs)
            {
                try
                {
                    var output1 = (IDXGIOutput1)output;
                    int hr = output1.DuplicateOutput(_device, out var duplication);
                    if (hr < 0)
                    {
                        string why = hr switch
                        {
                            D3D.E_ACCESSDENIED => "access denied (another capture holds the display, or this is the secure desktop)",
                            D3D.DXGI_ERROR_UNSUPPORTED => "not supported on this display adapter or session (remote desktop?)",
                            D3D.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE => "too many applications are already duplicating the desktop",
                            _ => $"0x{hr:X8}",
                        };
                        throw new InvalidOperationException("Desktop Duplication is unavailable: " + why);
                    }
                    duplication.GetDesc(out var dd);
                    if (dd.Format != D3D.DXGI_FORMAT_B8G8R8A8_UNORM && dd.Format != D3D.DXGI_FORMAT_B8G8R8X8_UNORM)
                    {
                        MF.Release(duplication);
                        throw new InvalidOperationException($"Desktop Duplication delivers pixel format {dd.Format}, which this capture does not handle (HDR display?).");
                    }
                    if (dd.Rotation > 1)
                    {
                        MF.Release(duplication);
                        throw new InvalidOperationException("Desktop Duplication of a rotated display is not handled.");
                    }
                    var texDesc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = dd.Width, Height = dd.Height, MipLevels = 1, ArraySize = 1,
                        Format = D3D.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, SampleQuality = 0,
                        Usage = D3D.D3D11_USAGE_STAGING, BindFlags = 0, CpuAccessFlags = D3D.D3D11_CPU_ACCESS_READ, MiscFlags = 0,
                    };
                    MF.Check(_device.CreateTexture2D(texDesc, IntPtr.Zero, out var staging), "CreateTexture2D (staging)");
                    _outputs.Add(new Output
                    {
                        Duplication = duplication, Staging = staging,
                        X = rect.Left - Left, Y = rect.Top - Top,
                        Width = Math.Min((int)dd.Width, rect.Right - rect.Left), Height = Math.Min((int)dd.Height, rect.Bottom - rect.Top),
                    });
                }
                finally { MF.Release(output); }
            }
        }
        finally { MF.Release(adapter); }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the desktop to change. Returns true when it did and the changed
    /// output(s) were copied into <paramref name="bgra"/> (Width×Height pixels, Width*4 bytes per row); false when
    /// nothing was drawn in time (pointer movement alone does not count). Throws
    /// <see cref="DesktopDuplicationLostException"/> when the duplication must be recreated.
    /// </summary>
    public bool TryCapture(int timeoutMs, byte[] bgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgra.Length < Width * Height * 4) throw new ArgumentException("Buffer too small for the virtual screen.");
        bool changed = false;
        for (int i = 0; i < _outputs.Count; i++)
        {
            var o = _outputs[i];
            int hr = o.Duplication.AcquireNextFrame((uint)(i == 0 ? Math.Max(0, timeoutMs) : 0), out var info, out var resource);
            if (hr == D3D.DXGI_ERROR_WAIT_TIMEOUT) continue;
            if (hr == D3D.DXGI_ERROR_ACCESS_LOST) throw new DesktopDuplicationLostException("The desktop changed (lock screen, resolution or session switch).");
            if (hr == D3D.DXGI_ERROR_INVALID_CALL) throw new DesktopDuplicationLostException("Desktop Duplication rejected the call; recreating.");
            MF.Check(hr, "AcquireNextFrame");
            bool drawn = info.LastPresentTime != 0;
            try
            {
                if (drawn && resource != null) _context.CopyResource((ID3D11Resource)o.Staging, (ID3D11Resource)resource);
            }
            finally
            {
                MF.Release(resource);
                o.Duplication.ReleaseFrame();
            }
            if (!drawn) continue;
            MF.Check(_context.Map((ID3D11Resource)o.Staging, 0, D3D.D3D11_MAP_READ, 0, out var mapped), "Map staging texture");
            try
            {
                int rows = Math.Min(o.Height, Height - o.Y), cols = Math.Min(o.Width, Width - o.X);
                if (rows > 0 && cols > 0)
                {
                    fixed (byte* dst = bgra)
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            Buffer.MemoryCopy((byte*)mapped.Data + (long)row * mapped.RowPitch, dst + ((long)(o.Y + row) * Width + o.X) * 4, (long)cols * 4, (long)cols * 4);
                        }
                    }
                }
            }
            finally { _context.Unmap((ID3D11Resource)o.Staging, 0); }
            changed = true;
        }
        return changed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var o in _outputs)
        {
            try { o.Duplication.ReleaseFrame(); } catch { }
            MF.Release(o.Duplication);
            MF.Release(o.Staging);
        }
        _outputs.Clear();
        MF.Release(_context);
        MF.Release(_device);
        _context = null!;
        _device = null!;
    }
}
