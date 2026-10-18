using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Startup with a journal on disk: clean exits, crashes, interrupted unlocks and corrupt files.</summary>
public sealed class LockEngineRecoveryTests
{
    [Fact]
    public void NoJournalMeansNothingToRecover()
    {
        var world = new LockWorld();
        world.Mics.Add("a");

        Assert.Null(world.Engine.Initialize());
        Assert.False(world.Engine.HasLock);
        Assert.False(world.Engine.JournalWasCorrupt);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
    }

    [Fact]
    public void CleanExitWhileLockedResumesQuietlyAtNextStart()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Mics.Add("b", muted: true);
        world.Engine.Initialize();
        world.Engine.Lock();
        DateTimeOffset lockedAt = world.Clock.UtcNow;

        world.Engine.MarkCleanExit();
        Assert.True(world.PersistedJournal!.ExitedCleanlyWhileLocked);
        world.Clock.AdvanceSeconds(3600);
        StartupRecovery? recovery = world.Restart();

        Assert.NotNull(recovery);
        Assert.True(recovery.ExitedCleanly);
        Assert.Equal(LockPhase.Locked, recovery.Phase);
        Assert.Equal(lockedAt, recovery.LockedSince);
        Assert.Equal(1, recovery.DevicesChanged);
        Assert.True(world.Engine.HasLock);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        // The flag is consumed: a crash after this start is not mistaken for a clean exit.
        Assert.False(world.PersistedJournal!.ExitedCleanlyWhileLocked);
        Assert.False(world.Restart()!.ExitedCleanly);

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
        Assert.True(world.Mics["b"].Muted);
    }

    [Fact]
    public void MarkCleanExitDoesNothingWhenNotFullyLocked()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.MarkCleanExit();
        Assert.Null(world.PersistedJournal);

        world.Crash.ArmAt(3);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
        world.Restart();
        world.Engine.MarkCleanExit();
        Assert.False(world.PersistedJournal!.ExitedCleanlyWhileLocked);
    }

    [Fact]
    public void CrashWhileLockedKeepsTheLockAndRepairsWhatChangedWhileWeWereGone()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();

        // Killed; meanwhile another app unmuted the mic and a new mic was plugged in.
        world.Mics.OtherAppSetsMute("a", false);
        world.Mics.Add("new");
        StartupRecovery? recovery = world.Restart();

        Assert.NotNull(recovery);
        Assert.False(recovery.ExitedCleanly);
        Assert.Equal(LockPhase.Locked, recovery.Phase);
        Assert.True(world.Mics["a"].Muted);
        Assert.True(world.Mics["new"].Muted);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
        Assert.False(world.Mics["new"].Muted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrashRightAfterAMuteCanBeFinishedOrRolledBack(bool finish)
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Engine.Initialize();
            return world;
        }

        // Killed right after the first mic was muted, before the journal recorded the result.
        int point = CrashPlan.Find(Setup, w => w.Engine.Lock(), "after SetMute(a, True)");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
        Assert.True(world.Mics["a"].Muted);
        Assert.False(world.Mics["b"].Muted);

        StartupRecovery recovery = world.Restart()!;
        Assert.Equal(LockPhase.Locking, recovery.Phase);
        Assert.False(recovery.ExitedCleanly);
        Assert.Equal(LockState.Locking, world.Engine.Status.State);

        if (finish)
        {
            world.Engine.ResumeLock();
            Assert.Equal(LockState.Locked, world.Engine.Status.State);
            Assert.True(world.Mics["b"].Muted);
        }

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
        Assert.False(world.Mics["b"].Muted);
    }

    [Fact]
    public void AfterACrashWhileLockingTheMicsAlreadyMutedStayMutedUntilTheUserChooses()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Engine.Initialize();
            return world;
        }

        // Killed after "a" was muted and verified, before "b" was reached. The recovery dialog waits for the user.
        int point = CrashPlan.Find(Setup, w => w.Engine.Lock(), "before SetMute(b, True)");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
        Assert.Equal(LockPhase.Locking, world.Restart()!.Phase);

        // Meanwhile an app unmutes "a": muted again at once, and by the safety poll if the notification is missed.
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        Assert.True(world.Mics["a"].Muted);
        world.Mics.OtherAppSetsMute("a", false);
        world.Mics.DropNotifications();
        world.Engine.Reconcile();
        Assert.True(world.Mics["a"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));

        // Nothing else changes until the user chooses: "b" was not reached, and a new mic is left alone.
        world.Mics.Add("new", notify: true);
        world.Pump();
        world.Engine.Reconcile();
        Assert.False(world.Mics["b"].Muted);
        Assert.False(world.Mics["new"].Muted);
        Assert.Equal(LockState.Locking, world.Engine.Status.State);

        world.Engine.Unlock(); // "Restore my devices"
        Assert.All(world.Mics.All, m => Assert.False(m.Muted, m.ToString()));
    }

    [Fact]
    public void AfterACrashWhileUnlockingNothingIsMutedAgainUntilTheUserChooses()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Engine.Initialize();
            world.Engine.Lock();
            return world;
        }

        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "after SetMute(b, False)");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        Assert.Equal(LockPhase.Unlocking, world.Restart()!.Phase);

        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["a"].Muted);
        Assert.False(world.Mics["b"].Muted);
        Assert.Empty(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
    }

    [Fact]
    public void LockAfterAnInterruptedUnlockLocksEverythingAgain()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Engine.Initialize();
            world.Engine.Lock();
            return world;
        }

        // Unlock is killed after the last-locked mic was unmuted and journaled.
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "after SetMute(b, False)") + 2;
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        Assert.False(world.Mics["b"].Muted);
        StartupRecovery recovery = world.Restart()!;
        Assert.Equal(LockPhase.Unlocking, recovery.Phase);
        Assert.Equal(LockState.Unlocking, world.Engine.Status.State);

        // "Stay locked".
        Assert.Equal(LockState.Locked, world.Engine.Lock().State);
        Assert.True(world.Mics["a"].Muted);
        Assert.True(world.Mics["b"].Muted);

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
        Assert.False(world.Mics["b"].Muted);
    }

    [Fact]
    public void StayingLockedAfterAnInterruptedUnlockKeepsAnAbsentDevicesRestore()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("usb");
            world.Mics.Add("other");
            world.Engine.Initialize();
            world.Engine.Lock();
            world.Mics.Unplug("usb");
            world.Pump();
            return world;
        }

        // Unlock is killed just before it deletes the journal ("usb" is RevertPending in it).
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "before journal delete");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        world.Restart();
        world.Engine.ResumeLock();
        Assert.True(world.Mics["other"].Muted);

        // Unlocked later, still without the device. It must still be restored when it returns.
        world.Engine.Unlock();
        world.Mics.Plug("usb");
        world.Pump();

        Assert.False(world.Mics["usb"].Muted);
        Assert.False(world.Mics["other"].Muted);
    }

    [Fact]
    public void StalePendingEntryFromAnInterruptedUnlockNeverOverridesTheUser()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("usb");
            world.Engine.Initialize();
            world.Engine.Lock();
            world.Mics.Unplug("usb");
            world.Pump();
            return world;
        }

        // Unlock is killed after it saved the pending restore but before it deleted the journal.
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "before journal delete");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        world.Restart();
        Assert.Single(world.Pending.Load());

        // "Stay locked"; the device comes back during the lock and the user unlocks normally.
        world.Engine.ResumeLock();
        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Unlock();
        Assert.False(world.Mics["usb"].Muted);

        // Later the user mutes it on purpose. Nothing may unmute it behind their back.
        world.Mics.OtherAppSetsMute("usb", true);
        world.Pump();
        world.Engine.Reconcile();
        world.Restart();

        Assert.True(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
        Assert.Empty(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrashBetweenJournalAndPendingStoreDuringLockIsHarmless(bool unlockDirectly)
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("usb");
            world.Engine.Initialize();
            world.Engine.Lock();
            world.Mics.Unplug("usb");
            world.Engine.Unlock();

            // It comes back but the notification is lost before the next lock.
            world.Mics.Plug("usb");
            world.Mics.DropNotifications();
            return world;
        }

        // The new lock takes over the pending entry and is killed before the pending store is rewritten.
        int point = CrashPlan.Find(Setup, w => w.Engine.Lock(), "before pending save");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
        Assert.Single(world.Pending.Load());
        world.Restart();

        if (!unlockDirectly)
        {
            world.Engine.ResumeLock();
        }

        world.Engine.Unlock();
        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());

        world.Mics.OtherAppSetsMute("usb", true);
        world.Engine.Reconcile();
        Assert.True(world.Mics["usb"].Muted);
    }

    [Fact]
    public void TransientPendingStoreReadFailureDuringLockDoesNotLoseRestores()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Mics.Add("usb", muted: true, state: EndpointState.NotPresent);
        world.SeedPendingRestore("usb");
        var flaky = new FlakyPendingRestoreStore(world.Pending);
        var engine = new LockEngine(world.Mics, world.Journal, flaky, world.Clock, () => world.Settings);
        engine.Initialize();

        flaky.FailLoads = true;
        engine.Lock();
        flaky.FailLoads = false;

        Assert.Single(world.Pending.Load());
        engine.Unlock();
        world.Mics.Plug("usb");
        engine.OnEndpointChanged("usb");
        Assert.False(world.Mics["usb"].Muted);
    }

    [Fact]
    public void CorruptJournalIsReportedAndDoesNotBlockANewLock()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.MemoryJournal.Corrupt = true;

        Assert.Null(world.Engine.Initialize());
        Assert.True(world.Engine.JournalWasCorrupt);
        Assert.False(world.Engine.HasLock);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);

        world.MemoryJournal.Corrupt = false;
        world.Engine.Lock();
        Assert.True(world.Mics["a"].Muted);
        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void CorruptJournalStillFinishesPendingRestores()
    {
        var world = new LockWorld();
        world.Mics.Add("usb", muted: true);
        world.SeedPendingRestore("usb");
        world.MemoryJournal.Corrupt = true;

        world.Engine.Initialize();

        Assert.False(world.Mics["usb"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
    }

    [Fact]
    public void FailedDeferredRestoreIsReportedOnceAndDropped()
    {
        var world = new LockWorld();
        world.Mics.Add("usb", muted: true).SetMuteThrows = true;
        world.SeedPendingRestore("usb");

        world.Engine.Initialize();

        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.True(alert.IsUrgent);
        Assert.Contains("Sound settings", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("..", alert.Message, StringComparison.Ordinal);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void DevicesChangedCountsEverythingThatMayStillBeMutedByUs()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Mics.Add("already", muted: true);
            world.Mics.Add("gone", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
            world.Engine.Initialize();
            world.Engine.Lock();
            return world;
        }

        Assert.Equal(2, Setup().Restart()!.DevicesChanged);

        // Interrupted unlock (reverse order): "b" already unmuted and journaled, "a" still muted.
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "after SetMute(b, False)") + 2;
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        StartupRecovery recovery = world.Restart()!;

        Assert.Equal(LockPhase.Unlocking, recovery.Phase);
        Assert.Equal(1, recovery.DevicesChanged);
    }

    [Fact]
    public void FailedUnmuteIsRetriedWhenAnInterruptedUnlockIsFinished()
    {
        static LockWorld Setup()
        {
            var world = new LockWorld();
            world.Mics.Add("a");
            world.Mics.Add("b");
            world.Engine.Initialize();
            world.Engine.Lock();
            world.Mics["b"].SetMuteThrows = true;
            return world;
        }

        // "b" could not be unmuted, then the unlock was killed before it finished.
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "before journal delete");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        Assert.Equal(ActionStatus.RevertFailed, world.Action("b").Status);

        // The device works again by the time the user finishes the unlock.
        world.Mics["b"].SetMuteThrows = false;
        world.Restart();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Mics["b"].Muted);
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void ResumeLockWithoutAJournalDoesNothing()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();

        world.Engine.ResumeLock();

        Assert.False(world.Engine.HasLock);
        Assert.False(world.Mics["a"].Muted);
    }
}
