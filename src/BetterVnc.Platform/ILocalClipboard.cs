using BetterVnc.Agent.Protocol;

namespace BetterVnc.Platform;

/// <summary>
/// The clipboard of the machine the viewer runs on: the operations the guest agent serves from its
/// <see cref="IGuestClipboard"/> (one implementation per OS serves both), plus a change notification. Every member may
/// be called from any thread and may block.
/// </summary>
public interface ILocalClipboard : IGuestClipboard
{
    /// <summary>
    /// The content changed, through any application, this one included. Raised on a background thread. The first handler
    /// starts the watch: a listener where the OS has one, otherwise polling <see cref="IGuestClipboard.GetChangeToken"/>
    /// every <see cref="IGuestClipboard.PollInterval"/>.
    /// </summary>
    event Action? Changed;
}

/// <summary>Raises a clipboard's change event by polling its change token, for OSes that offer no notification.</summary>
internal sealed class ClipboardPoller
{
    private readonly IGuestClipboard _clipboard;
    private readonly object _lock = new();
    private Action? _handlers;
    private System.Threading.Timer? _timer;
    private string? _last;
    private int _busy;

    public ClipboardPoller(IGuestClipboard clipboard) => _clipboard = clipboard;

    public void Add(Action? handler)
    {
        lock (_lock)
        {
            _handlers += handler;
            if (_timer != null || _handlers == null) return;
            _last = Token();
            _timer = new System.Threading.Timer(_ => Poll(), null, _clipboard.PollInterval, _clipboard.PollInterval);
        }
    }

    public void Remove(Action? handler)
    {
        lock (_lock)
        {
            _handlers -= handler;
            if (_handlers != null) return;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // the helper tool is slower than the interval
        try
        {
            var token = Token();
            Action? handlers;
            lock (_lock)
            {
                if (token == _last) return;
                _last = token;
                handlers = _handlers;
            }
            handlers?.Invoke();
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private string? Token()
    {
        try { return _clipboard.GetChangeToken(); }
        catch { return null; }
    }
}
