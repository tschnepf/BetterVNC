using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterVnc.AvaloniaSpike;

/// <summary>Timing and CPU bookkeeping shared by the tests, and the result line format.</summary>
internal sealed class Run
{
    public string Name { get; }
    private readonly int _uiThreadId;
    private readonly Dictionary<int, long> _submitted = new(); // marker value -> QPC when handed to the presenter
    private readonly List<double> _uiCosts = new();
    private readonly object _lock = new();
    private TimeSpan _cpu0;
    private Dictionary<int, TimeSpan> _threads0 = new();
    private long _t0;
    public int Submitted { get; private set; }

    public Run(string name, int uiThreadId) { Name = name; _uiThreadId = uiThreadId; }

    /// <summary>OS thread id -> label, for the test's own threads (call <see cref="NameThisThread"/> on each).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Named = new();
    public static void NameThisThread(string label) => Named[GetCurrentThreadId()] = label;

    public void Begin()
    {
        var p = Process.GetCurrentProcess();
        _cpu0 = p.TotalProcessorTime;
        _threads0 = Threads(p);
        _t0 = Stopwatch.GetTimestamp();
        lock (_lock) { _submitted.Clear(); _uiCosts.Clear(); Submitted = 0; }
    }

    /// <summary>The frame carrying <paramref name="marker"/> was just handed to the presentation path.</summary>
    public void Submit(int marker)
    {
        lock (_lock) { _submitted[marker] = Stopwatch.GetTimestamp(); Submitted++; }
    }

    public void UiCost(double ms) { lock (_lock) _uiCosts.Add(ms); }

    public string End(ScreenProbe? probe, string? extra = null)
    {
        double wall = (Stopwatch.GetTimestamp() - _t0) / (double)Stopwatch.Frequency;
        var p = Process.GetCurrentProcess();
        p.Refresh();
        double cpu = (p.TotalProcessorTime - _cpu0).TotalSeconds;
        var threads = Threads(p);
        var perThread = threads.Select(kv => (Id: kv.Key, Cpu: (kv.Value - (_threads0.TryGetValue(kv.Key, out var v) ? v : TimeSpan.Zero)).TotalSeconds))
            .OrderByDescending(x => x.Cpu).ToList();
        double ui = perThread.FirstOrDefault(x => x.Id == _uiThreadId).Cpu;

        var sb = new StringBuilder();
        sb.AppendLine($"[{Name}]");
        sb.AppendLine($"  submitted {Submitted} frames in {wall:0.0} s ({Submitted / wall:0.0}/s)");
        sb.AppendLine($"  process CPU {cpu / wall * 100:0.0}% of one core; UI thread {ui / wall * 100:0.0}%; " +
            string.Join(", ", perThread.Where(x => x.Id != _uiThreadId).Take(4).Select(x => $"{(Named.TryGetValue(x.Id, out var n) ? n : "thread")} {x.Cpu / wall * 100:0.0}%")));
        lock (_lock)
        {
            if (_uiCosts.Count > 0)
            {
                var c = _uiCosts.OrderBy(x => x).ToList();
                sb.AppendLine($"  UI-thread work per frame: median {c[c.Count / 2]:0.00} ms, p95 {c[(int)(c.Count * 0.95)]:0.00} ms, max {c[^1]:0.00} ms");
            }
            if (probe != null)
            {
                var seen = probe.Snapshot();
                var lat = new List<double>();
                foreach (var (marker, at) in seen)
                    if (_submitted.TryGetValue(marker, out var sub) && at >= sub && at - sub < Stopwatch.Frequency)
                        lat.Add((at - sub) * 1000.0 / Stopwatch.Frequency);
                lat.Sort();
                sb.AppendLine($"  on screen: {seen.Count} distinct frames ({seen.Count / wall:0.0}/s) of {Submitted} submitted; desktop frames {probe.DesktopFrames}, unreadable {probe.UnreadableFrames}" +
                    (probe.Failure != null ? $"; probe FAILED {probe.Failure}" : ""));
                if (lat.Count > 0)
                    sb.AppendLine($"  submit -> on screen: median {lat[lat.Count / 2]:0.0} ms, p95 {lat[(int)(lat.Count * 0.95)]:0.0} ms, max {lat[^1]:0.0} ms ({lat.Count} matched)");
            }
        }
        if (extra != null) sb.AppendLine("  " + extra);
        return sb.ToString().TrimEnd();
    }

    private static Dictionary<int, TimeSpan> Threads(Process p)
    {
        var d = new Dictionary<int, TimeSpan>();
        foreach (ProcessThread t in p.Threads)
        {
            try { d[t.Id] = t.TotalProcessorTime; } catch { }
        }
        return d;
    }

    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
}
