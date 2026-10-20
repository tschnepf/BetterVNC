using System.Windows.Threading;
using BetterVnc.Viewer.Core;

namespace BetterVnc.App.Services;

/// <summary>The WPF dispatcher as the viewer core's <see cref="IUiThread"/>.</summary>
public sealed class WpfUiThread : IUiThread
{
    private readonly Dispatcher _dispatcher;

    public WpfUiThread(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action, UiPriority priority = UiPriority.Normal) =>
        _dispatcher.BeginInvoke(priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal, action);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    public T Invoke<T>(Func<T> func) => _dispatcher.Invoke(func);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(_dispatcher, interval, tick);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(Dispatcher dispatcher, TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public bool IsEnabled => _timer.IsEnabled;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
