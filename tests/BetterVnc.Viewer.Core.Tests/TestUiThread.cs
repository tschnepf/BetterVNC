using System.Collections.Concurrent;
using BetterVnc.Viewer.Core;

namespace BetterVnc.Viewer.Core.Tests;

/// <summary>
/// A UI thread for tests: one thread working through a queue, with a synchronization context so awaits continue on it,
/// as on WPF's dispatcher. Its timers never fire on their own; a test fires them with <see cref="TestTimer.FireAsync"/>.
/// </summary>
public sealed class TestUiThread : IUiThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly List<TestTimer> _timers = new();
    private readonly ConcurrentQueue<Exception> _errors = new();

    public TestUiThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { _errors.Enqueue(ex); }
        }
    }

    // ------------------------------------------------------------------ IUiThread

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { } // disposed
    }

    public Task InvokeAsync(Action action) => RunAsync(() => { action(); return true; });

    public T Invoke<T>(Func<T> func) => CheckAccess() ? func() : RunAsync(func).GetAwaiter().GetResult();

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new TestTimer(this, interval, tick);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    // ------------------------------------------------------------------ for tests

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Runs asynchronous <paramref name="work"/> on the UI thread; its awaits continue there.</summary>
    public Task RunAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { await work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Waits until <paramref name="condition"/>, checked on the UI thread, holds.</summary>
    public async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await RunAsync(condition))
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
        ThrowIfFailed();
    }

    /// <summary>The newest timer created with <paramref name="interval"/>.</summary>
    public TestTimer Timer(TimeSpan interval)
    {
        lock (_timers) return _timers.Last(t => t.Interval == interval);
    }

    /// <summary>Rethrows the first exception that escaped work on the UI thread.</summary>
    public void ThrowIfFailed()
    {
        if (_errors.TryPeek(out var ex)) throw new InvalidOperationException("Work on the UI thread failed: " + ex.Message, ex);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(5000);
    }

    private sealed class Context : SynchronizationContext
    {
        private readonly TestUiThread _ui;
        public Context(TestUiThread ui) => _ui = ui;
        public override void Post(SendOrPostCallback d, object? state) => _ui.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => _ui.Invoke(() => { d(state); return true; });
        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>A timer of <see cref="TestUiThread"/>: started and stopped by the code under test, fired by the test.</summary>
public sealed class TestTimer : IUiTimer
{
    private readonly TestUiThread _ui;
    private readonly Action _tick;

    public TestTimer(TestUiThread ui, TimeSpan interval, Action tick)
    {
        _ui = ui;
        Interval = interval;
        _tick = tick;
    }

    public TimeSpan Interval { get; }
    public bool IsEnabled { get; private set; }
    public void Start() => IsEnabled = true;
    public void Stop() => IsEnabled = false;

    /// <summary>One tick, as if the interval had passed; nothing while the timer is stopped.</summary>
    public Task FireAsync() => _ui.InvokeAsync(() => { if (IsEnabled) _tick(); });
}
