using System.Diagnostics;
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
/// drawn, so an unchanged screen costs nothing and "nothing changed" is known for free, and it says which rectangles
/// were redrawn or moved, so only those need copying. Every attached output of the primary adapter is duplicated and
/// the frames are composed into one BGRA image of the virtual screen.
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
        /// <summary>What the current frame changed, in this output's coordinates.</summary>
        public DirtyRegion Changed = null!;
        /// <summary>The next frame must be copied whole: the first one, or the caller's picture buffer is new.</summary>
        public bool CopyAll = true;
    }

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private readonly List<Output> _outputs = new();
    private bool _primaryOnly;
    private bool _disposed;
    private byte[]? _picture;
    private IntPtr _metadata;
    private uint _metadataSize;

    /// <summary>Virtual-screen origin and size covered by the captured outputs.</summary>
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OutputCount => _outputs.Count;
    /// <summary>Time the last <see cref="TryCapture(int, byte[], DirtyRegion?)"/> spent reading rectangles and copying pixels, without the wait.</summary>
    public long LastCopyMicros { get; private set; }

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
                    int width = Math.Min((int)dd.Width, rect.Right - rect.Left), height = Math.Min((int)dd.Height, rect.Bottom - rect.Top);
                    _outputs.Add(new Output
                    {
                        Duplication = duplication, Staging = staging,
                        X = rect.Left - Left, Y = rect.Top - Top, Width = width, Height = height,
                        Changed = new DirtyRegion(Math.Max(1, width), Math.Max(1, height)),
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
    public bool TryCapture(int timeoutMs, byte[] bgra) => TryCapture(timeoutMs, bgra, null);

    /// <summary>
    /// Like <see cref="TryCapture(int, byte[])"/>, but with <paramref name="changed"/> only what the compositor reports
    /// as redrawn or moved since the previous capture is copied, from the GPU and into <paramref name="bgra"/>, and those
    /// rectangles (whole 16×16 tiles, virtual-screen coordinates from <see cref="Left"/>/<see cref="Top"/>) are added to
    /// <paramref name="changed"/>. Pass the same buffer every time: the rest of it is left as the earlier captures made
    /// it. The first capture, and the first into a different buffer, copies everything.
    /// </summary>
    public bool TryCapture(int timeoutMs, byte[] bgra, DirtyRegion? changed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgra.Length < Width * Height * 4) throw new ArgumentException("Buffer too small for the virtual screen.");
        if (!ReferenceEquals(bgra, _picture))
        {
            _picture = bgra;
            foreach (var o in _outputs) o.CopyAll = true;
        }
        LastCopyMicros = 0;
        bool any = false;
        for (int i = 0; i < _outputs.Count; i++)
        {
            var o = _outputs[i];
            int hr = o.Duplication.AcquireNextFrame((uint)(i == 0 ? Math.Max(0, timeoutMs) : 0), out var info, out var resource);
            if (hr == D3D.DXGI_ERROR_WAIT_TIMEOUT) continue;
            if (hr == D3D.DXGI_ERROR_ACCESS_LOST) throw new DesktopDuplicationLostException("The desktop changed (lock screen, resolution or session switch).");
            if (hr == D3D.DXGI_ERROR_INVALID_CALL) throw new DesktopDuplicationLostException("Desktop Duplication rejected the call; recreating.");
            MF.Check(hr, "AcquireNextFrame");
            long started = Stopwatch.GetTimestamp();
            try { any |= CopyFrame(o, info, resource, bgra, changed); }
            finally { LastCopyMicros += (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency; }
        }
        return any;
    }

    /// <summary>Copies what an acquired frame changed (all of it without <paramref name="changed"/>); releases the frame.</summary>
    private bool CopyFrame(Output o, in DXGI_OUTDUPL_FRAME_INFO info, IDXGIResource? resource, byte[] bgra, DirtyRegion? changed)
    {
        bool drawn = info.LastPresentTime != 0 && resource != null;
        o.Changed.Clear();
        try
        {
            if (drawn)
            {
                // Without the compositor's rectangles (or when asked to) the whole output counts as changed.
                if (changed == null || o.CopyAll || !ReadChangedRects(o, info)) o.Changed.AddAll();
                if (o.Changed.IsFull) _context.CopyResource((ID3D11Resource)o.Staging, (ID3D11Resource)resource!);
                else
                {
                    foreach (var r in o.Changed.GetRects())
                    {
                        var box = new D3D11_BOX { Left = (uint)r.X, Top = (uint)r.Y, Front = 0, Right = (uint)r.Right, Bottom = (uint)r.Bottom, Back = 1 };
                        _context.CopySubresourceRegion((ID3D11Resource)o.Staging, 0, (uint)r.X, (uint)r.Y, 0, (ID3D11Resource)resource!, 0, &box);
                    }
                }
            }
        }
        finally
        {
            MF.Release(resource);
            o.Duplication.ReleaseFrame();
        }
        if (!drawn || o.Changed.IsEmpty) return false;
        MF.Check(_context.Map((ID3D11Resource)o.Staging, 0, D3D.D3D11_MAP_READ, 0, out var mapped), "Map staging texture");
        try
        {
            fixed (byte* dst = bgra)
            {
                foreach (var r in o.Changed.GetRects())
                {
                    int rows = Math.Min(r.Height, Height - o.Y - r.Y), cols = Math.Min(r.Width, Width - o.X - r.X);
                    if (rows <= 0 || cols <= 0) continue;
                    byte* from = (byte*)mapped.Data + (long)r.Y * mapped.RowPitch + (long)r.X * 4;
                    byte* to = dst + ((long)(o.Y + r.Y) * Width + o.X + r.X) * 4;
                    for (int row = 0; row < rows; row++)
                        Buffer.MemoryCopy(from + (long)row * mapped.RowPitch, to + (long)row * Width * 4, (long)cols * 4, (long)cols * 4);
                    changed?.Add(o.X + r.X, o.Y + r.Y, cols, rows);
                }
            }
        }
        finally { _context.Unmap((ID3D11Resource)o.Staging, 0); }
        o.CopyAll = false;
        return true;
    }

    /// <summary>
    /// Adds the frame's moved and dirty rectangles to the output's region. False when there are none to read or reading
    /// fails, in which case the caller copies everything.
    /// </summary>
    private bool ReadChangedRects(Output o, in DXGI_OUTDUPL_FRAME_INFO info)
    {
        if (info.TotalMetadataBufferSize == 0) return false;
        EnsureMetadata(info.TotalMetadataBufferSize);
        uint required;
        int hr = o.Duplication.GetFrameMoveRects(_metadataSize, _metadata, out required);
        if (hr == D3D.DXGI_ERROR_MORE_DATA)
        {
            EnsureMetadata(required);
            hr = o.Duplication.GetFrameMoveRects(_metadataSize, _metadata, out required);
        }
        if (hr < 0) return false;
        // A move leaves the destination with new pixels; the uncovered source is reported as dirty.
        var moves = (DXGI_OUTDUPL_MOVE_RECT*)_metadata;
        for (int k = 0; k < required / sizeof(DXGI_OUTDUPL_MOVE_RECT); k++)
        {
            var d = moves[k].DestinationRect;
            o.Changed.Add(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top);
        }
        hr = o.Duplication.GetFrameDirtyRects(_metadataSize, _metadata, out required);
        if (hr == D3D.DXGI_ERROR_MORE_DATA)
        {
            EnsureMetadata(required);
            hr = o.Duplication.GetFrameDirtyRects(_metadataSize, _metadata, out required);
        }
        if (hr < 0) return false;
        var dirty = (RECT*)_metadata;
        for (int k = 0; k < required / sizeof(RECT); k++)
            o.Changed.Add(dirty[k].Left, dirty[k].Top, dirty[k].Right - dirty[k].Left, dirty[k].Bottom - dirty[k].Top);
        return true;
    }

    private void EnsureMetadata(uint size)
    {
        if (size <= _metadataSize) return;
        if (_metadata != IntPtr.Zero) Marshal.FreeHGlobal(_metadata);
        _metadataSize = Math.Max(size, 4096);
        _metadata = Marshal.AllocHGlobal((int)_metadataSize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_metadata != IntPtr.Zero) Marshal.FreeHGlobal(_metadata);
        _metadata = IntPtr.Zero;
        _metadataSize = 0;
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
