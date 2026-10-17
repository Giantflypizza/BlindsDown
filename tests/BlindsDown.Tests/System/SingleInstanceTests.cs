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
}
