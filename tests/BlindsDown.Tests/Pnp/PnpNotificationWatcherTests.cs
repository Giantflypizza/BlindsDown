using System.Collections.Concurrent;
using BlindsDown.Devices.Pnp;
using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.Pnp;

/// <summary>
/// The watcher's debouncing and lifetime, driven through <see cref="PnpNotificationWatcher.OnNativeEvent"/>, so no
/// notification is registered with Windows. The real registration is covered by <see cref="RealPnpTests"/>.
/// </summary>
public sealed class PnpNotificationWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly EngineThread _engine;

    public PnpNotificationWatcherTests()
    {
        _engine = new EngineThread("BlindsDown PnP watcher test", _errors.Enqueue);
    }

    [Fact]
    public async Task ABurstOfEventsRaisesOneChangedOnTheEngineThread()
    {
        using var watcher = new PnpNotificationWatcher(_engine, Debounce);
        var raised = new SemaphoreSlim(0);
        int count = 0;
        bool onEngineThread = true;
        watcher.Changed += () =>
        {
            onEngineThread &= _engine.IsCurrentThread;
            Interlocked.Increment(ref count);
            raised.Release();
        };

        // A webcam arriving: dozens of events within a few milliseconds.
        for (int i = 0; i < 50; i++)
        {
            watcher.OnNativeEvent();
        }

        Assert.True(await raised.WaitAsync(Wait, TestContext.Current.CancellationToken));
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.True(onEngineThread);

        // Later events raise it again: no change is ever swallowed.
        watcher.OnNativeEvent();
        Assert.True(await raised.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref count));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task NothingIsRaisedAfterDispose()
    {
        var watcher = new PnpNotificationWatcher(_engine, Debounce);
        int count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);

        watcher.OnNativeEvent(); // Armed, then disposed before the timer fires.
        watcher.Dispose();
        watcher.OnNativeEvent();
        await Task.Delay(Debounce * 3, TestContext.Current.CancellationToken);
        await _engine.InvokeAsync(() => { });

        Assert.Equal(0, Volatile.Read(ref count));
        Assert.Empty(_errors);
    }

    [Fact]
    public void DisposeIsSafeTwiceFromAnyThreadAndStartAfterDisposeDoesNothing()
    {
        var watcher = new PnpNotificationWatcher(_engine, Debounce);
        watcher.Dispose();
        watcher.Dispose();
        _engine.Invoke(
            () =>
            {
                watcher.Start(); // Disposed: registers nothing with Windows.
                watcher.Dispose();
                return true;
            },
            Wait);

        Assert.Equal(0, watcher.RegistrationCount);
        Assert.Empty(_errors);
    }

    [Fact]
    public void EveryWatcherHasItsOwnContext()
    {
        using var first = new PnpNotificationWatcher(_engine);
        using var second = new PnpNotificationWatcher(_engine);

        Assert.NotEqual(first.Context, second.Context);
        Assert.NotEqual(0, first.Context);
    }

    public void Dispose() => _engine.Dispose();
}
