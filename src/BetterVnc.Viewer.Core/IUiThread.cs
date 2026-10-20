namespace BetterVnc.Viewer.Core;

/// <summary>How urgently posted work runs: <see cref="Background"/> yields to input and rendering.</summary>
public enum UiPriority
{
    Normal,
    Background,
}

/// <summary>
/// The UI thread of whatever framework shows the viewer (WPF's Dispatcher today): the services in this library raise
/// their events and run their timers there. Awaiting on the UI thread continues on it, as with any UI framework's
/// synchronization context.
/// </summary>
public interface IUiThread
{
    /// <summary>True on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> on the UI thread and returns at once.</summary>
    void Post(Action action, UiPriority priority = UiPriority.Normal);

    /// <summary>Runs <paramref name="action"/> on the UI thread; the task completes when it has run.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Runs <paramref name="func"/> on the UI thread and waits for it (inline when already there).</summary>
    T Invoke<T>(Func<T> func);

    /// <summary>A stopped timer that calls <paramref name="tick"/> on the UI thread, at background priority.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A repeating timer on the UI thread; use it from the UI thread only.</summary>
public interface IUiTimer
{
    /// <summary>Time between ticks; a change applies from the next start.</summary>
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    void Start();
    void Stop();
}
