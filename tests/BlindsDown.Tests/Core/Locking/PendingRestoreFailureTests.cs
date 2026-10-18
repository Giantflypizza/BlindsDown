using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// The list of restores owed to microphones that were away at unlock (pending-restores.json) can't always be read or
/// written: another program may hold it, or the disk is full. Unlock and startup must still work, and nothing owed may
/// be lost or done twice.
/// </summary>
public sealed class PendingRestoreFailureTests
{
    [Fact]
    public void UnlockWorksWhenTheListCantBeSavedAndSavesItLater()
    {
        (LockWorld world, IPendingRestoreStore saved, FlakyPendingRestoreStore flaky) = FlakyWorld();
        world.Mics.Add("a");
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Pump();

        flaky.FailSaves = true;
        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Engine.HasLock);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
        Assert.Equal(1, report.PendingCount);
        Assert.False(world.Mics["a"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed), a => a.DeviceName == "restore list");
        Assert.Empty(saved.Load());
        Assert.NotNull(world.Journal.Load()); // Kept: a restart still owes "usb" its unmute.

        // Still failing: tried on every safety poll, said once.
        world.Engine.Reconcile();
        world.Clock.AdvanceSeconds(60);
        world.Engine.Reconcile();
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed), a => a.DeviceName == "restore list");

        // There is room again: the next safety poll saves the list and tidies up.
        flaky.FailSaves = false;
        world.Engine.Reconcile();
        Assert.Equal("usb", Assert.Single(saved.Load()).TargetId);
        Assert.Null(world.Journal.Load());

        world.Mics.Plug("usb");
        world.Pump();
        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(saved.Load());
    }

    [Fact]
    public void RestartBeforeTheListCouldBeSavedStillRestoresTheMicWhenItReturns()
    {
        (LockWorld world, IPendingRestoreStore saved, FlakyPendingRestoreStore flaky) = FlakyWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Pump();
        flaky.FailSaves = true;
        world.Engine.Unlock();

        flaky.FailSaves = false;
        StartupRecovery recovery = world.Restart()!;
        Assert.Equal(LockPhase.Unlocking, recovery.Phase);
        world.Engine.Unlock(); // "Restore my devices"

        Assert.Equal("usb", Assert.Single(saved.Load()).TargetId);
        world.Mics.Plug("usb");
        world.Pump();
        Assert.False(world.Mics["usb"].Muted);
    }

    [Fact]
    public void StartupWorksWhenTheListCantBeSavedAndAFinishedRestoreIsNeverDoneAgain()
    {
        (LockWorld world, IPendingRestoreStore saved, FlakyPendingRestoreStore flaky) = FlakyWorld();
        world.Mics.Add("usb", muted: true);
        world.SeedPendingRestore("usb");
        flaky.FailSaves = true;

        Assert.Null(world.Engine.Initialize());

        Assert.False(world.Mics["usb"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));

        // The user mutes it. The file still lists it, but its restore is done: it is never unmuted again.
        world.Mics.OtherAppSetsMute("usb", true);
        for (int i = 0; i < 3; i++)
        {
            world.Engine.Reconcile();
            world.Clock.AdvanceSeconds(5);
        }

        Assert.True(world.Mics["usb"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
        Assert.Single(saved.Load());

        flaky.FailSaves = false;
        world.Engine.Reconcile();
        Assert.Empty(saved.Load());
        Assert.True(world.Mics["usb"].Muted);
    }

    [Fact]
    public void ListThatCantBeOpenedAtUnlockIsNotOverwritten()
    {
        // "usb" is owed an unmute by an earlier lock (it was away). The next unlock finds "headset" away, while
        // another program holds the list.
        (LockWorld world, IPendingRestoreStore saved, FlakyPendingRestoreStore flaky) = FlakyWorld();
        world.Mics.Add("a");
        world.Mics.Add("usb", muted: true, state: EndpointState.NotPresent);
        world.Mics.Add("headset", unreadableWhenUnplugged: true);
        world.SeedPendingRestore("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("headset", EndpointState.Unplugged);
        world.Pump();

        flaky.FailLoads = true;
        world.Engine.Unlock();
        Assert.Equal("usb", Assert.Single(saved.Load()).TargetId);
        Assert.NotNull(world.Journal.Load());

        flaky.FailLoads = false;
        world.Engine.Reconcile();
        Assert.Equal(["headset", "usb"], saved.Load().Select(p => p.TargetId).Order(StringComparer.Ordinal));
        Assert.Null(world.Journal.Load());

        world.Mics.Plug("usb");
        world.Mics.Plug("headset");
        world.Pump();
        Assert.False(world.Mics["usb"].Muted);
        Assert.False(world.Mics["headset"].Muted);
        Assert.Empty(saved.Load());
    }

    [Fact]
    public void LockAndNewMicsWorkWhenTheListCantBeSaved()
    {
        // An earlier lock still owes "usb" its unmute; it is back, and the list can't be saved when this lock
        // takes that over, nor when "late" (also owed one) arrives during the lock.
        (LockWorld world, IPendingRestoreStore saved, FlakyPendingRestoreStore flaky) = FlakyWorld();
        world.Mics.Add("usb", muted: true);
        world.Mics.Add("late", muted: true, state: EndpointState.NotPresent);
        world.SeedPendingRestore("usb");
        world.SeedPendingRestore("late");
        var engine = new LockEngine(world.Mics, world.Journal, flaky, world.Clock, () => world.Settings);
        engine.AlertRaised += world.Alerts.Add;
        flaky.FailSaves = true;

        Assert.Equal(LockState.Locked, engine.Lock().State);
        world.Mics.Plug("late");
        engine.OnEndpointChanged("late");
        Assert.True(world.Mics["late"].Muted);
        Assert.Equal(2, saved.Load().Count); // Not saved: both still listed.

        flaky.FailSaves = false;
        engine.Unlock();

        Assert.False(world.Mics["usb"].Muted);
        Assert.False(world.Mics["late"].Muted);
        Assert.Empty(saved.Load());
    }

    /// <summary>A world whose pending-restore store can be made to fail; <c>Saved</c> is what is on disk.</summary>
    private static (LockWorld World, IPendingRestoreStore Saved, FlakyPendingRestoreStore Flaky) FlakyWorld()
    {
        var world = new LockWorld();
        IPendingRestoreStore saved = world.Pending;
        var flaky = new FlakyPendingRestoreStore(saved);
        world.UseStores(world.Journal, flaky);
        return (world, saved, flaky);
    }
}
