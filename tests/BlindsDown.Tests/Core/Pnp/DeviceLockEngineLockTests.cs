using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Tests.Core.Pnp;

public sealed class DeviceLockEngineLockTests
{
    [Fact]
    public void LockTurnsOffEveryPresentColourCameraAndVerifiesIt()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("cam2", type: CameraType.ColorAndInfrared);
        world.Devices.Add("cam3", type: null);
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(status.Problems);
        foreach (string id in new[] { "cam1", "cam2", "cam3" })
        {
            Assert.True(world.Devices[id].Disabled, id);
            Assert.False(world.Devices[id].DisabledPersistently, id + " must never be disabled persistently");
            Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus(id).Protection);
            LockAction action = world.Action(id);
            Assert.Equal(ActionKind.DisableDevice, action.Kind);
            Assert.Equal(ActionStatus.Applied, action.Status);
            Assert.False(action.WasAlreadyLocked);
        }

        Assert.True(world.Engine.HasLock);
        Assert.Equal(LockPhase.Locked, world.PersistedJournal!.Phase);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void UnlockTurnsBackOnExactlyWhatTheLockTurnedOffAndDeletesTheJournal()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1", name: "HD Webcam C270");
        world.Devices.AddMic("mic1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.False(world.Devices["mic1"].Disabled);
        Assert.True(report.AllExact);
        Assert.True(report.CoversCameras);
        Assert.Equal(2, report.DeviceCount);
        Assert.Equal("All 2 devices restored exactly as before.", report.Summary);
        Assert.Equal(
            new[]
            {
                new RestoreLine(RestoreOutcome.Exact, "HD Webcam C270", "Turned back on, as before the lock.", DeviceKind.Camera),
                new RestoreLine(RestoreOutcome.Exact, "Microphone mic1", "Turned back on, as before the lock.", DeviceKind.MicrophoneHardware),
            },
            report.Lines);
        Assert.Null(world.PersistedJournal);
        Assert.False(world.Engine.HasLock);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("cam1").Protection);
    }

    [Fact]
    public void UnlockWhenNotLockedReturnsNull()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();

        Assert.Null(world.Engine.Unlock());
        Assert.Empty(world.Devices.EnableCalls);
    }

    [Fact]
    public void ADeviceTheUserDisabledIsRecordedAndNeverTurnedOn()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("off", disabledByUser: true);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("off").Protection);
        Assert.True(world.Action("off").WasAlreadyLocked);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Devices["off"].Disabled);
        Assert.True(world.Devices["off"].DisabledPersistently);
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("off")));
        Assert.Equal(0, world.Devices.EnableCallsFor(world.Id("off")));
        Assert.Contains(report.Lines, l => l.DeviceName == "Camera off" && l.Outcome == RestoreOutcome.Exact && l.Message == "Off, as before the lock.");
    }

    [Fact]
    public void ADeviceThatIsNotConnectedIsShownAndLeftAlone()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("away", present: false);
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.Locked, status.State);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("away").Protection);
        Assert.False(world.HasAction("away"));
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("away")));
    }

    [Fact]
    public void AVetoedDisableIsAProblemWithOneBusyAlertAndIsRetriedOnEveryReconcile()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1", name: "HD Webcam C270");
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.PartiallyLocked, status.State);
        DeviceLockInfo camera = world.DeviceStatus("cam1");
        Assert.Equal(DeviceProtection.None, camera.Protection);
        Assert.Equal("In use by an app; BlindsDown keeps trying.", camera.Problem);
        Assert.Equal(new[] { "HD Webcam C270: In use by an app; BlindsDown keeps trying." }, status.Problems);
        EngineAlert busy = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.DeviceBusy, busy.Kind);
        Assert.Equal("HD Webcam C270", busy.DeviceName);
        Assert.Equal("A camera is in use by an app, so Windows won't turn it off yet. BlindsDown keeps trying: HD Webcam C270.", busy.Message);
        Assert.True(busy.IsUrgent);

        // Still in use: tried again on every safety poll, but the user is told only once per lock.
        world.Engine.Reconcile();
        world.Engine.Reconcile();
        Assert.Equal(3, world.Devices.DisableCallsFor(world.Id("cam1")));
        Assert.Single(world.Alerts);

        // Device notifications don't retry it (each attempt could cause more notifications).
        world.Engine.OnDevicesChanged();
        Assert.Equal(3, world.Devices.DisableCallsFor(world.Id("cam1")));

        // The app lets go.
        world.Devices["cam1"].DisableAlways = null;
        world.Engine.Reconcile();

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("cam1").Protection);
        Assert.Empty(world.Engine.Status.Problems);
        Assert.Equal(ActionStatus.Applied, world.Action("cam1").Status);

        world.Engine.Unlock();
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void AVetoThenSuccessOnTheRetryLocksTheDevice()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableResults.Enqueue(PnpChangeResult.Vetoed);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Equal(ActionStatus.ApplyFailed, world.Action("cam1").Status);

        world.Engine.Reconcile();

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
    }

    [Theory]
    [InlineData(PnpChangeResult.NeedsRestart, "Windows can only turn it off after a restart. Restart the PC to lock it.")]
    [InlineData(PnpChangeResult.AccessDenied, "Windows didn't allow BlindsDown to turn it off.")]
    [InlineData(PnpChangeResult.Failed, "Windows couldn't turn it off (simulated Failed).")]
    public void OtherFailuresAreExplainedAlertedOnceAndRetriedOnlyEveryMinute(PnpChangeResult result, string problem)
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableAlways = result;
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
        Assert.Equal(problem, world.DeviceStatus("cam1").Problem);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.LockFailed, alert.Kind);
        Assert.Equal($"BlindsDown couldn't turn off a camera: Camera cam1. {problem}", alert.Message);

        world.Clock.AdvanceSeconds(30);
        world.Engine.Reconcile();
        Assert.Equal(1, world.Devices.DisableCallsFor(world.Id("cam1")));

        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();
        Assert.Equal(2, world.Devices.DisableCallsFor(world.Id("cam1")));
        Assert.Single(world.Alerts);
    }

    [Fact]
    public void ABackendThatThrowsIsAFailureNotACrash()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableThrows = true;
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal("Windows couldn't turn it off (CM_Disable_DevNode returned CR_FAILURE (simulated)).", world.DeviceStatus("cam1").Problem);
        Assert.Equal(ActionStatus.ApplyFailed, world.Action("cam1").Status);
    }

    [Fact]
    public void ADisableThatDoesNotStickIsNotALock()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableDoesNotStick = true;
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
        Assert.Equal("Windows said it was turned off, but it is still on.", world.DeviceStatus("cam1").Problem);
        Assert.Equal(ActionStatus.ApplyFailed, world.Action("cam1").Status);
    }

    [Fact]
    public void AFailedEnableIsReportedAndRetriedUntilTheDeviceIsOnAgain()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableResults.Enqueue(PnpChangeResult.Failed);

        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Equal("Could not be turned back on (simulated Failed). Restart the PC to turn it back on.", line.Message);
        Assert.Equal(1, report.ProblemCount);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Null(world.PersistedJournal);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
        Assert.Equal(DeviceLockEngine.OwedRestoreProblem, world.DeviceStatus("cam1").Problem);

        // Not before the retry interval.
        world.Engine.Reconcile();
        Assert.True(world.Devices["cam1"].Disabled);

        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
        Assert.Null(world.DeviceStatus("cam1").Problem);
        Assert.Empty(world.Engine.Status.Problems);
    }

    [Fact]
    public void AnOwedRestoreIsTakenOverByTheNextLockSoItsUnlockTurnsTheDeviceOn()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableResults.Enqueue(PnpChangeResult.Failed);
        world.Engine.Unlock();
        Assert.True(world.Devices["cam1"].Disabled);

        // Locked again while it is still off because of BlindsDown: it must not count as "already off".
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.False(world.Action("cam1").WasAlreadyLocked);
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("cam1").Protection);

        world.Engine.Unlock();

        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void AnOwedRestoreIsDroppedWhenTheDeviceIsUnpluggedOrTurnedOnByOthers()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("cam2");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam1"].EnableAlways = PnpChangeResult.Failed;
        world.Devices["cam2"].EnableAlways = PnpChangeResult.Failed;
        world.Engine.Unlock();

        world.Devices.Unplug("cam1");
        world.Devices.SomeoneEnables("cam2");
        world.Clock.AdvanceSeconds(31);
        world.Pump();
        world.Engine.Reconcile();

        Assert.Empty(world.Engine.Status.Problems);
        Assert.Empty(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
        Assert.Equal(1, world.Devices.EnableCallsFor(world.Id("cam1")));
    }

    [Fact]
    public void AnUnpluggedDeviceNeedsNothingAtUnlock()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices.Unplug("cam1");
        world.Pump();

        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Exact, line.Outcome);
        Assert.Equal("Not connected right now; it will work when plugged back in.", line.Message);
        Assert.Empty(world.Devices.EnableCalls);

        world.Devices.Plug("cam1");
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ADeviceSomeoneTurnedOnBeforeTheUnlockIsSimplyOn()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices.SomeoneEnables("cam1"); // Not delivered yet.

        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal("On, as before the lock.", Assert.Single(report.Lines).Message);
        Assert.Empty(world.Devices.EnableCalls);
    }

    [Fact]
    public void ADeviceWindowsNoLongerListsIsAskedForDirectlyAtUnlock()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        // The backend stops listing it (e.g. its camera interface is gone while disabled), but it is still off.
        world.Devices["cam1"].Kind = DeviceKind.Microphone;

        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Devices["cam1"].Disabled);
        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Exact, line.Outcome);
        Assert.Equal(DeviceKind.Camera, line.Kind); // From the journal: Windows no longer says.
    }

    [Fact]
    public void RestoreLinesSayWhichKindOfDeviceEachIs()
    {
        var world = new PnpWorld();
        world.Devices.AddMic("mic1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.Unplug("mic1");
        world.Pump();

        RestoreLine line = Assert.Single(world.Engine.Unlock()!.Lines);

        Assert.Equal(DeviceKind.MicrophoneHardware, line.Kind);
    }

    [Fact]
    public void WhenWindowsCantListTheDevicesTheLockIsPartialAndKeepsTrying()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Devices.EnumerateThrows = true;
        world.Engine.Reconcile();

        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
        Assert.Contains(DeviceLockEngine.ListFailedProblem, world.Engine.Status.Problems);
        Assert.NotEmpty(world.Engine.Status.Devices); // The last list is still shown.

        world.Devices.EnumerateThrows = false;
        world.Engine.Reconcile();

        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Empty(world.Engine.Status.Problems);
    }

    [Fact]
    public void ALockThatCantListTheDevicesYetLocksThemQuietlyOnceItCan()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("cam2");
        world.Engine.Initialize();
        world.Devices.EnumerateThrows = true;

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.True(world.Engine.HasLock);
        Assert.Empty(world.Devices.DisableCalls);

        world.Devices.EnumerateThrows = false;
        world.Engine.Reconcile();

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.True(world.Devices["cam2"].Disabled);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Empty(world.AlertsOf(EngineAlertKind.NewDeviceLocked));

        world.Engine.Unlock();
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.False(world.Devices["cam2"].Disabled);
    }

    [Fact]
    public void IfTheJournalCantBeWrittenNothingIsLocked()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.UseJournal(new FailingDeviceJournalStore { FailAfter = 0 });
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Engine.HasLock);
        Assert.Equal(LockState.Unlocked, status.State);
        Assert.Empty(world.Devices.DisableCalls);
        Assert.Contains(status.Problems, p => p.StartsWith("BlindsDown can't save its lock record (There is not enough space on the disk.)", StringComparison.Ordinal));
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));

        // Unlock (the tray app's "unlock" still arrives) clears the notice.
        Assert.Null(world.Engine.Unlock());
        Assert.Empty(world.Engine.Status.Problems);
    }

    [Fact]
    public void ADeviceIsNeverTurnedOffWhenItsWriteAheadRecordFails()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.UseJournal(new FailingDeviceJournalStore { FailAfter = 1 });
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Empty(world.Devices.DisableCalls);
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
        Assert.Equal(DeviceLockEngine.NotSavedProblem, world.DeviceStatus("cam1").Problem);
        Assert.Contains(world.AlertsOf(EngineAlertKind.LockFailed), a => a.DeviceName == "lock record");
    }

    [Fact]
    public void AJournalThatCantBeDeletedStillUnlocks()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.UseJournal(new FailingDeviceJournalStore { FailAfter = int.MaxValue, FailDeletes = true });
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(report.AllExact);
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.False(world.Engine.HasLock);
        Assert.Contains(world.Alerts, a => a.Message.StartsWith("The camera lock is off, but BlindsDown couldn't delete its lock record", StringComparison.Ordinal));
    }

    [Fact]
    public void ALockRecordAnUnlockCouldNotDeleteIsDeletedLaterAndNeverBringsTheLockBack()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        var store = new FailingDeviceJournalStore { FailAfter = int.MaxValue };
        world.UseJournal(store);
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        // The disk stops taking writes during the unlock, so the record on disk still says "locked".
        store.FailAfter = 0;
        store.FailDeletes = true;
        world.Engine.Unlock();
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Equal(LockPhase.Locked, world.PersistedJournal!.Phase);
        int alerts = world.Alerts.Count;

        // Deleting is retried on every pass (without more alerts) until it works.
        world.Clock.AdvanceSeconds(31);
        world.Engine.Reconcile();
        Assert.NotNull(world.PersistedJournal);
        store.FailDeletes = false;
        world.Engine.Reconcile();

        Assert.Null(world.PersistedJournal);
        Assert.Equal(alerts, world.Alerts.Count);
        Assert.False(world.Restart());
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Empty(world.Devices.DisableCalls.Skip(1));
    }

    [Fact]
    public void StatusListsEveryDeviceWhileUnlocked()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("ir", type: CameraType.Infrared);
        world.Devices.Add("off", disabledByUser: true);
        world.Devices.Add("away", present: false);
        world.Devices.AddMic("mic1");
        world.Devices.Add("vcam", type: CameraType.Virtual, isVirtual: true);

        Assert.False(world.Engine.Initialize());

        DeviceLockStatus status = world.Engine.Status;
        Assert.Equal(LockState.Unlocked, status.State);
        Assert.Equal(6, status.Devices.Count);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("cam1").Protection);
        Assert.Equal(CameraType.Infrared, world.DeviceStatus("ir").Camera);
        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("off").Protection);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("away").Protection);
        Assert.Equal(DeviceKind.MicrophoneHardware, world.DeviceStatus("mic1").Kind);
        Assert.Null(world.DeviceStatus("mic1").Camera);
        Assert.True(world.DeviceStatus("vcam").IsVirtual);
        Assert.Equal(CameraType.Virtual, world.DeviceStatus("vcam").Camera);
        Assert.Equal(world.Devices["cam1"].ToPnpDevice().StableKey, world.DeviceStatus("cam1").StableKey);
        Assert.Empty(status.Problems);
        Assert.All(status.Devices, d => Assert.False(d.HasHardwareMute));
    }

    [Fact]
    public void StatusChangedIsRaisedOnlyWhenSomethingChanged()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        int count = world.Statuses.Count;

        world.Engine.Reconcile();
        world.Engine.OnDevicesChanged();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.Equal(count, world.Statuses.Count);

        world.Devices.Add("cam2");
        world.Engine.Reconcile();

        Assert.True(world.Statuses.Count > count);
        Assert.Equal(LockState.Locked, world.Statuses[^1].State);
    }

    [Fact]
    public void LockingAgainWithTheSameRequestOnlyRechecks()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Guid lockId = world.PersistedJournal!.LockId;
        int saves = world.MemoryJournal.SaveCount;

        world.Engine.Lock(PnpWorld.CamerasOnly with { });

        Assert.Equal(lockId, world.PersistedJournal!.LockId);
        Assert.Equal(saves, world.MemoryJournal.SaveCount);
        Assert.Equal(1, world.Devices.DisableCallsFor(world.Id("cam1")));
    }

    [Fact]
    public void NoDeviceIsEverDisabledPersistently()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.AddMic("mic1");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.Everything);

        world.Devices.Reboot();

        Assert.False(world.Devices["cam1"].Disabled);
        Assert.False(world.Devices["mic1"].Disabled);
    }
}
