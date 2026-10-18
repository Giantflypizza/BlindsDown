using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.SystemServices;

/// <summary>Uses a unique kernel-object prefix per test, so a real running BlindsDown is never involved.</summary>
public sealed class SingleInstanceTests
{
    private readonly string _prefix = "BlindsDownTests-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void OnlyTheFirstLaunchAcquires()
    {
        using SingleInstance? first = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(first);

        using SingleInstance? second = SingleInstance.TryAcquire(_prefix);
        Assert.Null(second);
    }

    [Fact]
    public void ReleasedInstanceCanBeAcquiredAgain()
    {
        SingleInstance? first = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(first);
        first.Dispose();

        using SingleInstance? again = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task CommandsAreForwardedToTheRunningInstance()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);
        var received = new TaskCompletionSource<InstanceCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.CommandReceived += command => received.TrySetResult(command);

        // Sent before Listen: the auto-reset event keeps it until the listener starts.
        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Toggle));
        instance.Listen();

        Assert.Equal(InstanceCommand.Toggle, await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EachCommandHasItsOwnChannel()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);
        var seen = new List<InstanceCommand>();
        var bothSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.CommandReceived += command =>
        {
            lock (seen)
            {
                seen.Add(command);
                if (seen.Count == 2)
                {
                    bothSeen.TrySetResult();
                }
            }
        };
        instance.Listen();

        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Lock));
        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Show));
        await bothSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        lock (seen)
        {
            Assert.Equal([InstanceCommand.Show, InstanceCommand.Lock], seen.Order());
        }
    }

    [Fact]
    public void AHandlerThatThrowsDoesNotCrashTheProcess()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);
        using var handled = new ManualResetEventSlim();
        instance.CommandReceived += _ =>
        {
            handled.Set();
            throw new InvalidOperationException("Handler failure");
        };
        instance.Listen();

        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Unlock));
        Assert.True(handled.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SendingWithoutARunningInstanceFails() =>
        Assert.False(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Toggle));

    [Fact]
    public void ACommandLineModeIsTheOnlyInstanceButTakesNoCommands()
    {
        // --restore, --selftest: a launch meanwhile must not be told its command was delivered.
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix, takesCommands: false);
        Assert.NotNull(instance);

        using SingleInstance? second = SingleInstance.TryAcquire(_prefix);
        Assert.Null(second);
        Assert.False(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Unlock));
    }

    [Fact]
    public void NothingIsDeliveredAfterStopListening()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);
        int received = 0;
        instance.CommandReceived += _ => Interlocked.Increment(ref received);
        instance.Listen();

        instance.StopListening();

        Assert.False(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Lock));
        Assert.False(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Show));
        Assert.Null(SingleInstance.TryAcquire(_prefix)); // Still the only one until it ends.
        Assert.Equal(0, Volatile.Read(ref received));
    }

    [Fact]
    public void StopListeningWithoutListenClosesTheCommandsToo()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);

        instance.StopListening();
        instance.Listen(); // Too late: stopping is for good.

        Assert.False(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Toggle));
    }

    [Fact]
    public void ACommandSentInTheLastMomentIsReportedByStopListening()
    {
        // Sent while the instance takes commands, but not handed on yet (nobody listens on this one): the sender was told
        // it was delivered, so StopListening says which commands that happened to.
        using SingleInstance? instance = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(instance);
        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Unlock));

        IReadOnlyList<InstanceCommand> late = instance.StopListening();

        Assert.Equal([InstanceCommand.Unlock], late);
        Assert.Empty(instance.StopListening()); // Once.
    }

    [Fact]
    public void TheNextLaunchGetsTheInstanceOnceACommandLineModeEnds()
    {
        SingleInstance? restore = SingleInstance.TryAcquire(_prefix, takesCommands: false);
        Assert.NotNull(restore);
        restore.Dispose();

        using SingleInstance? next = SingleInstance.TryAcquire(_prefix);
        Assert.NotNull(next);
        Assert.True(SingleInstance.SendToRunningInstance(_prefix, InstanceCommand.Toggle));
    }
}
