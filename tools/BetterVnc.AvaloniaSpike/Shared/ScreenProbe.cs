using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// Watches the real screen with DXGI Desktop Duplication and reads the <see cref="Marker"/> at given screen points, so
/// the time a frame was handed to the presentation path can be compared with the time DWM put it on screen
/// (<c>LastPresentTime</c>, QPC, the same clock as <see cref="Stopwatch.GetTimestamp"/>).
/// </summary>
internal sealed class ScreenProbe : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly int _outputLeft, _outputTop;
    private ID3D11Texture2D? _staging;
    private (int X, int Y)[] _points = Array.Empty<(int, int)>();
    private (int Left, int Top, int Right, int Bottom) _box;
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Marker value -> QPC time of the first desktop frame that showed it.</summary>
    public Dictionary<int, long> FirstSeen { get; } = new();
    public int DesktopFrames { get; private set; }
    public int UnreadableFrames { get; private set; }
    public string? Failure { get; private set; }

    private ScreenProbe(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, int left, int top)
    {
        _device = device; _context = context; _duplication = duplication; _outputLeft = left; _outputTop = top;
    }

    /// <summary><see cref="Create"/>, or null with the reason on the console (the PC is locked, or Click to Do is open).</summary>
    public static ScreenProbe? TryCreate(int screenX, int screenY)
    {
        try { return Create(screenX, screenY); }
        catch (Exception ex)
        {
            Console.WriteLine($"  (no screen probe: {ex.Message.Split('\n')[0].Trim()}; on-screen numbers unavailable)");
            return null;
        }
    }

    /// <summary>Duplicates the output that contains the screen point (physical pixels).</summary>
    public static ScreenProbe Create(int screenX, int screenY)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var r = output.Description.DesktopCoordinates;
                        if (screenX < r.Left || screenX >= r.Right || screenY < r.Top || screenY >= r.Bottom) continue;
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        var duplication = output1.DuplicateOutput(device!);
                        return new ScreenProbe(device!, context!, duplication, r.Left, r.Top);
                    }
                }
            }
        }
        throw new InvalidOperationException($"no output contains {screenX},{screenY}");
    }

    /// <summary>The 18 block centres of the marker on screen, in physical screen pixels. Any thread.</summary>
    public void Watch((int X, int Y)[] points)
    {
        lock (_lock)
        {
            _points = points.Select(p => (p.X - _outputLeft, p.Y - _outputTop)).ToArray();
            _box = (_points.Min(p => p.X), _points.Min(p => p.Y), _points.Max(p => p.X) + 1, _points.Max(p => p.Y) + 1);
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(_box.Right - _box.Left), Height = (uint)(_box.Bottom - _box.Top), MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            FirstSeen.Clear();
            DesktopFrames = UnreadableFrames = 0;
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "screen probe", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>A copy of what was seen so far.</summary>
    public Dictionary<int, long> Snapshot() { lock (_lock) return new Dictionary<int, long>(FirstSeen); }

    private unsafe void Run()
    {
        BetterVnc.AvaloniaSpike.Run.NameThisThread("probe");
        var grey = new byte[Marker.Blocks];
        while (!_stop)
        {
            var result = _duplication.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;
            if (result.Failure) { Failure = $"AcquireNextFrame 0x{result.Code:X8}"; return; }
            try
            {
                if (info.LastPresentTime == 0) continue; // only the pointer moved
                lock (_lock)
                {
                    if (_staging == null) continue;
                    using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                    _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0,
                        new Vortice.Mathematics.Box(_box.Left, _box.Top, 0, _box.Right, _box.Bottom, 1));
                    var map = _context.Map(_staging, 0, MapMode.Read);
                    try
                    {
                        for (int k = 0; k < _points.Length && k < grey.Length; k++)
                        {
                            byte* p = (byte*)map.DataPointer + (_points[k].Y - _box.Top) * map.RowPitch + (_points[k].X - _box.Left) * 4;
                            grey[k] = (byte)((p[0] + p[1] + p[2]) / 3);
                        }
                    }
                    finally { _context.Unmap(_staging, 0); }
                    DesktopFrames++;
                    if (Marker.Read(grey) is { } value) FirstSeen.TryAdd(value, info.LastPresentTime);
                    else UnreadableFrames++;
                }
            }
            finally
            {
                resource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
