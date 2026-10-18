using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>
/// Restores that can't simply happen at unlock: a device an unlock could not turn back on (owed, also across restarts
/// of the helper), a device someone else turned off for good in the meantime (theirs, left off), and a disable that
/// Windows made persistent (BlindsDown's to undo, also after a restart).
/// </summary>
public sealed class DeviceLockEngineOwedRestoreTests
{
    [Fact]
    public void AnOwedRestoreSurvivesAHelperRestartAndIsNotTakenForTheUsersOwnDisable()
    {
        PnpWorld world = UnlockedWithAFailedEnable();

        world.Restart(); // E.g. "Update the camera helper": the PC keeps running, so the camera is still off.

        Assert.Equal(DeviceProtection.None, world.DeviceStatus("cam1").Protection);
        Assert.Equal(DeviceLockEngine.OwedRestoreProblem, world.DeviceStatus("cam1").Problem);

        world.Devices["cam1"].EnableAlways = null;
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
        Assert.Empty(world.MemoryJournal.LoadOwedRestores());
    }

    [Fact]
    public void AnOwedRestoreIsTriedRightAwayWhenTheHelperStarts()
    {
        PnpWorld world = UnlockedWithAFailedEnable();
        world.Devices["cam1"].EnableAlways = null;

        world.Restart();

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Null(world.DeviceStatus("cam1").Problem);
    }

    [Fact]
    public void AfterAHelperRestartTheNextLockStillTurnsAnOwedDeviceBackOnAtItsUnlock()
    {
        PnpWorld world = UnlockedWithAFailedEnable();
        world.Restart();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Action("cam1").WasAlreadyLocked);
        Assert.Empty(world.MemoryJournal.LoadOwedRestores()); // The lock's record has it now.

        world.Devices["cam1"].EnableAlways = null;
        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Equal(RestoreOutcome.Exact, Assert.Single(report.Lines).Outcome);
    }

    [Fact]
    public void AnOwedDeviceTheNextLockLeavesOutIsShownAsBlindsDownsAndTurnedOnDuringTheLock()
    {
        PnpWorld world = UnlockedWithAFailedEnable();
        world.Devices.Add("cam2");

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("cam1")] });

        Assert.Equal(DeviceProtection.None, world.DeviceStatus("cam1").Protection);
        Assert.Equal(DeviceLockEngine.OwedRestoreProblem, world.DeviceStatus("cam1").Problem);
        Assert.Equal(LockState.Locked, world.Engine.Status.State); // It is off: nothing is unprotected because of it.

        world.Devices["cam1"].EnableAlways = null;
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("cam1").Protection);
        Assert.True(world.Devices["cam2"].Disabled);
    }

    [Fact]
    public void AnOwedDeviceTheLockCoversIsNeverTurnedOnWhileLockedEvenWhenTheLockCantRecordIt()
    {
        var store = new FailingDeviceJournalStore { FailAfter = int.MaxValue };
        var world = new PnpWorld();
        world.UseJournal(store);
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;
        world.Engine.Unlock();
        world.Devices["cam1"].EnableAlways = null;

        // The next lock starts before Windows can list the devices, and then the disk fills up.
        world.Devices.EnumerateThrows = true;
        world.Engine.Lock(PnpWorld.CamerasOnly);
        store.FailAfter = 0;
        world.Devices.EnumerateThrows = false;
        int enables = world.Devices.EnableCallsFor(world.Id("cam1"));
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();

        // The lock wants it off, and it is off: an owed restore must not turn a camera on during a lock.
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(enables, world.Devices.EnableCallsFor(world.Id("cam1")));

        // Still owed: once unlocked, it is turned back on.
        store.FailAfter = int.MaxValue;
        world.Engine.Unlock();
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ACrashAnywhereInAnUnlockThatCantTurnACameraOnStillGetsItTurnedOnLater()
    {
        int points = PnpWorld.CountCrashPoints(LockedWithAFailingEnable, w => w.Engine.Unlock());
        Assert.True(points > 5, $"Only {points} crash points.");
        for (int n = 1; n <= points + 1; n++)
        {
            PnpWorld world = LockedWithAFailingEnable();
            world.Crash.ArmAt(n);
            string context = $"crash at point {n}";
            try
            {
                world.Engine.Unlock();
                context = "no crash";
            }
            catch (SimulatedCrashException)
            {
                context += $" ({world.Crash.LastCrash})";
            }

            // The helper restarts (an update, say): the PC keeps running, so the camera is still off. A crash before
            // the unlock was recorded leaves the lock in place, and the user unlocks again.
            if (world.Restart())
            {
                world.Engine.Unlock();
            }

            Assert.False(world.Engine.HasLock, context);
            Assert.True(world.Devices["cam1"].Disabled, context);
            Assert.NotEqual(DeviceProtection.DisabledByUser, world.DeviceStatus("cam1").Protection);

            world.Devices["cam1"].EnableAlways = null;
            world.Clock.AdvanceSeconds(31);
            world.Engine.Reconcile();

            Assert.False(world.Devices["cam1"].Disabled, context);
            Assert.False(world.Devices["cam2"].Disabled, context);
            Assert.Empty(world.MemoryJournal.LoadOwedRestores());
        }
    }

    [Fact]
    public void UnlockingWhileNotLockedTriesOwedRestoresRightAwayAndReportsThem()
    {
        PnpWorld world = UnlockedWithAFailedEnable();

        // The uninstaller and --restore unlock too: they must learn that a device stays off.
        RestoreReport failed = world.Engine.Unlock()!;
        Assert.Equal(
            new RestoreLine(RestoreOutcome.Failed, "Camera cam1", "Could not be turned back on (simulated Failed). Restart the PC to turn it back on.", DeviceKind.Camera),
            Assert.Single(failed.Lines));
        Assert.True(failed.CoversCameras);

        world.Devices["cam1"].EnableAlways = null;
        RestoreReport done = world.Engine.Unlock()!;

        Assert.Equal(new RestoreLine(RestoreOutcome.Exact, "Camera cam1", "Turned back on, as before the lock.", DeviceKind.Camera), Assert.Single(done.Lines));
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Null(world.Engine.Unlock());
    }

    [Fact]
    public void OwedRestoresThatCouldNotBeSavedAreSavedOnTheNextPass()
    {
        var store = new FailingDeviceJournalStore { FailAfter = int.MaxValue, FailOwedSaves = true };
        var world = new PnpWorld();
        world.UseJournal(store);
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;
        world.Engine.Unlock();
        Assert.Empty(store.LoadOwedRestores());

        store.FailOwedSaves = false;
        world.Engine.Reconcile();

        OwedDevice owed = Assert.Single(store.LoadOwedRestores());
        Assert.Equal(world.Id("cam1"), owed.InstanceId);
        Assert.Equal("Camera cam1", owed.Name);
        Assert.Equal(DeviceKind.Camera, owed.Kind);
        Assert.False(owed.Persistent);
    }

    [Fact]
    public void ADeviceSomeoneTurnsOffForGoodDuringTheLockIsLeftOffWithoutAFailureOrRetries()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed; // In use: BlindsDown can't turn it off yet.
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        // The user turns it off in Device Manager instead, which lasts until they turn it back on.
        world.Devices.SomeoneDisables("cam1");
        world.Pump();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new RestoreLine(RestoreOutcome.ChangedByOthers, "Camera cam1", DeviceLockEngine.KeptOffLine, DeviceKind.Camera), Assert.Single(report.Lines));
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("cam1").Protection);
        Assert.Null(world.DeviceStatus("cam1").Problem);
        Assert.Empty(world.MemoryJournal.LoadOwedRestores());

        // Not asked again and again.
        int enables = world.Devices.EnableCallsFor(world.Id("cam1"));
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();
        Assert.Equal(enables, world.Devices.EnableCallsFor(world.Id("cam1")));

        // The next lock knows it is the user's.
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.True(world.Action("cam1").WasAlreadyLocked);
    }

    [Fact]
    public void ADeviceTurnedOffForGoodBeforeTheLockButStillRunningIsLeftOffAtUnlock()
    {
        // Turned off in Device Manager while an app had it open (Windows waits for a restart): it is running, so it
        // is locked, and at unlock it stays off as whoever turned it off asked. A restart would not change that.
        var world = new PnpWorld();
        world.Devices.Add("cam1").DisabledPersistently = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.True(world.Devices["cam1"].Disabled);

        RestoreLine line = Assert.Single(world.Engine.Unlock()!.Lines);

        Assert.Equal(RestoreOutcome.ChangedByOthers, line.Outcome);
        Assert.DoesNotContain("Restart", line.Message, StringComparison.Ordinal);
        Assert.Empty(world.MemoryJournal.LoadOwedRestores());
    }

    [Fact]
    public void ADeviceTurnedOffForGoodByOthersIsDroppedFromANarrowerLockAndLeftOffWithoutAnAlert()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.AddMic("mic1");
        world.Devices["mic1"].DisableAlways = PnpChangeResult.Vetoed;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.SomeoneDisables("mic1");
        world.Pump();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.True(world.Devices["mic1"].Disabled);
        Assert.False(world.HasAction("mic1"));
        Assert.Empty(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void ADisableWindowsMadePersistentIsUndoneAtUnlockEvenAfterARestart()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1").DisableMakesPersistent = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.True(world.PersistedJournal!.IsPersistent(world.Id("cam1")));

        // Persistent, so still off after the restart (the backend's own memory of it is gone with the helper).
        world.Reboot();
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("cam1").Protection);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(report.AllExact);
        Assert.Equal((false, false), (world.Devices["cam1"].Disabled, world.Devices["cam1"].DisabledPersistently));
        Assert.Equal(world.Id("cam1"), Assert.Single(world.Devices.ClearPersistentCalls));
    }

    [Fact]
    public void ANarrowerLockTurnsAPersistentDisableBackOnToo()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.AddMic("mic1").DisableMakesPersistent = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);
        world.Restart();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal((false, false), (world.Devices["mic1"].Disabled, world.Devices["mic1"].DisabledPersistently));
        Assert.Empty(world.PersistedJournal!.PersistentDisables);
    }

    [Fact]
    public void APersistentDisableThatCantBeUndoneSaysToUseDeviceManagerAndIsStillOwedAfterARestart()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1").DisableMakesPersistent = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;

        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal("Could not be turned back on (simulated Failed). " + DeviceLockEngine.DeviceManagerAdvice, Assert.Single(report.Lines).Message);
        Assert.Equal(DeviceLockEngine.OwedPersistentProblem, world.DeviceStatus("cam1").Problem);
        Assert.True(Assert.Single(world.MemoryJournal.LoadOwedRestores()).Persistent);

        world.Reboot(); // A restart doesn't help: still off, still owed.
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(DeviceLockEngine.OwedPersistentProblem, world.DeviceStatus("cam1").Problem);

        world.Devices["cam1"].EnableAlways = null;
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();

        Assert.Equal((false, false), (world.Devices["cam1"].Disabled, world.Devices["cam1"].DisabledPersistently));
    }

    [Fact]
    public void APersistentDisableOfADeviceUnpluggedAtUnlockIsUndoneWhenItIsBack()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1").DisableMakesPersistent = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices.Unplug("cam1");
        world.Pump();

        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new RestoreLine(RestoreOutcome.Pending, "Camera cam1", DeviceLockEngine.PersistentAwayLine, DeviceKind.Camera), Assert.Single(report.Lines));

        world.Devices.Plug("cam1"); // Comes back disabled: the flag stayed.
        world.Pump();

        Assert.Equal((false, false), (world.Devices["cam1"].Disabled, world.Devices["cam1"].DisabledPersistently));
    }

    [Fact]
    public void APersistentDisableOfADeviceUnpluggedWhenALockLeftItOutIsUndoneWhenItIsBack()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.AddMic("mic1").DisableMakesPersistent = true;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.Unplug("mic1");
        world.Pump();

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.False(world.HasAction("mic1"));

        world.Devices.Plug("mic1"); // Comes back disabled: the flag stayed.
        world.Pump();

        Assert.Equal((false, false), (world.Devices["mic1"].Disabled, world.Devices["mic1"].DisabledPersistently));
        Assert.True(world.Devices["cam1"].Disabled);
    }

    /// <summary>Two cameras locked; turning the first one back on fails until a test lets it work.</summary>
    private static PnpWorld LockedWithAFailingEnable()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("cam2");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;
        return world;
    }

    /// <summary>A camera locked, then an unlock whose enable failed: still off because of BlindsDown (and the enable
    /// keeps failing until a test lets it work).</summary>
    private static PnpWorld UnlockedWithAFailedEnable()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;
        world.Engine.Unlock();
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(DeviceLockEngine.OwedRestoreProblem, world.DeviceStatus("cam1").Problem);
        return world;
    }
}
