using System.Collections.Concurrent;

namespace BlindsDown.Infrastructure;

/// <summary>
/// One dedicated background thread (COM multithreaded apartment) that owns every Core Audio object
/// and runs the lock engine and the usage tracker. Everything that touches devices is posted here,
/// so that logic is single-threaded and never blocks the UI.
///
/// Windows audio notification callbacks arrive on system threads; they must only <see cref="Post"/>
/// work here and return immediately (never block, never call back into the audio API).
/// </summary>
internal sealed class EngineThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Action<Exception> _onError;
    private readonly List<System.Threading.Timer> _timers = [];
    private volatile bool _disposed;

    public EngineThread(string name, Action<Exception> onError)
    {
        _onError = onError;
        _thread = new Thread(Run)
        {
            Name = name,
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Queues work. Safe to call from any thread, including COM callbacks.</summary>
    public void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    public Task InvokeAsync(Action action) =>
        InvokeAsync(() =>
        {
            action();
            return true;
        });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="func"/> on the engine thread and waits for the result.
    /// Never call this from the engine thread itself or from a COM callback.</summary>
    public T Invoke<T>(Func<T> func, TimeSpan timeout)
    {
        if (IsCurrentThread)
        {
            return func();
        }

        Task<T> task = InvokeAsync(func);
        if (Task.WaitAny([task], timeout) < 0)
        {
            throw new TimeoutException("The device thread did not respond in time.");
        }

        // GetResult rethrows the original exception (not an AggregateException), so callers can catch
        // e.g. IOException or DeviceOperationException directly.
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="callback"/> on the engine thread every <paramref name="period"/>.</summary>
    public void Every(TimeSpan period, Action callback)
    {
        var timer = new System.Threading.Timer(_ => Post(callback), null, period, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_timers)
        {
            foreach (System.Threading.Timer timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        _queue.CompleteAdding();

        // Only dispose the queue once the thread has really finished with it. If it is stuck in a blocked
        // Windows call, leave the queue to the garbage collector (it is a background thread), otherwise the
        // thread would crash the process with ObjectDisposedException when the call finally returns.
        if (!IsCurrentThread && _thread.Join(TimeSpan.FromSeconds(5)))
        {
            _queue.Dispose();
        }
    }

    private void Run()
    {
        try
        {
            RunLoop();
        }
        catch (InvalidOperationException)
        {
            // The queue was completed or disposed while this thread was busy: just end the thread.
            // (ObjectDisposedException derives from InvalidOperationException.)
        }
    }

    private void RunLoop()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    _onError(ex);
                }
                catch (Exception)
                {
                    // The error handler itself failed; keep the device thread alive regardless.
                }
            }
        }
    }
}
