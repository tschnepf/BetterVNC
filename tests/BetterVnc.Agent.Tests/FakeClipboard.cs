using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tests;

/// <summary>In-memory stand-in for an OS clipboard: holds one data item or one file list and counts changes.</summary>
public sealed class FakeClipboard : IGuestClipboard
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _data = new();
    private List<string>? _files;
    private int _sequence;

    public string OsName => "fake";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(20);

    public int SetDataCalls { get; private set; }
    public IReadOnlyList<string>? LastSetFiles { get; private set; }
    public string? LastSetFormat { get; private set; }
    public byte[]? LastSetData { get; private set; }

    /// <summary>Simulates the guest user copying something.</summary>
    public void Put(string format, byte[] data)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = null;
            _data[format] = data;
            _sequence++;
        }
    }

    public void PutFiles(params string[] paths)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = paths.ToList();
            _sequence++;
        }
    }

    public string? GetChangeToken()
    {
        lock (_lock) return _sequence == 0 ? null : _sequence.ToString();
    }

    public IReadOnlyList<string> GetFormats()
    {
        lock (_lock)
        {
            var list = _data.Keys.ToList();
            if (_files != null) list.Add(AgentFormat.Files);
            return list;
        }
    }

    public byte[]? GetData(string format)
    {
        lock (_lock) return _data.TryGetValue(format, out var d) ? d : null;
    }

    public IReadOnlyList<string>? GetFilePaths()
    {
        lock (_lock) return _files;
    }

    public void SetData(string format, byte[] data)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = null;
            _data[format] = data;
            _sequence++;
            SetDataCalls++;
            LastSetFormat = format;
            LastSetData = data;
        }
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            _data.Clear();
            _files = paths.ToList();
            _sequence++;
            SetDataCalls++;
            LastSetFiles = paths;
        }
    }
}
