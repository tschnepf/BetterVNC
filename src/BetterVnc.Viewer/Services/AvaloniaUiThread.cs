using Avalonia.Threading;
using BetterVnc.Viewer.Core;

namespace BetterVnc.Viewer.Services;

/// <summary>Avalonia's UI thread dispatcher as the viewer core's <see cref="IUiThread"/>.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public static AvaloniaUiThread Instance { get; } = new();

    private static Dispatcher Dispatcher => Dispatcher.UIThread;

    public bool CheckAccess() => Dispatcher.CheckAccess();

    public void Post(Action action, UiPriority priority = UiPriority.Normal) =>
        Dispatcher.Post(action, priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal);

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).GetTask();

    public T Invoke<T>(Func<T> func) => Dispatcher.Invoke(func);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
