using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>
/// A lock request that changes while locked: widen or narrow the lock. The app itself never narrows a lock while
/// locked, so a narrower request comes from another program (or another session's BlindsDown), and it is never quiet.
/// </summary>
public sealed class DeviceLockEngineChangeTests
{
    [Fact]
    public void WideningLocksTheNewlyRequestedDevicesWithoutNewDeviceAlerts()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        Guid lockId = world.PersistedJournal!.LockId;

        world.Engine.Lock(new DeviceLockRequest { Cameras = true, MicHardware = true, IncludeInfrared = true });

        Assert.True(world.Devices["ir"].Disabled);
        Assert.True(world.Devices["mic"].Disabled);
        Assert.Empty(world.Alerts);
        Assert.Equal(lockId, world.PersistedJournal!.LockId);
        Assert.True(world.PersistedJournal.Request.IncludeInfrared);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
    }

    [Fact]
    public void NarrowingTurnsBackOnWhatIsNoLongerRequested()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(new DeviceLockRequest { Cameras = true, MicHardware = true, IncludeInfrared = true });

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Devices["ir"].Disabled);
        Assert.False(world.Devices["mic"].Disabled);
        Assert.True(world.Devices["cam"].Disabled);
        Assert.False(world.HasAction("ir"));
        Assert.False(world.HasAction("mic"));
        Assert.DoesNotContain(world.Id("mic"), world.PersistedJournal!.DeviceKinds.Keys);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("mic").Protection);
        Assert.Null(world.DeviceStatus("mic").Problem);

        // Never quietly: the app doesn't narrow a lock while locked, but any program can send such a request.
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.True(alert.IsUrgent);
        Assert.Equal(
            "A change to the lock left 2 devices out, so BlindsDown turned them back on: Camera ir, Microphone mic. If you didn't change the lock, unlock and lock again, and check which programs are running.",
            alert.Message);

        RestoreReport report = world.Engine.Unlock()!;
        Assert.Equal(1, report.DeviceCount);
        Assert.False(world.Devices["cam"].Disabled);
    }

    [Fact]
    public void ARequestThatLeavesTheCamerasOutTurnsThemBackOnWithAnUrgentAlertEveryTime()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        // What any program on the PC can send to the helper's pipe.
        world.Engine.Lock(new DeviceLockRequest { Cameras = false });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.True(world.Engine.HasLock);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.ReEnabledWhileLocked, alert.Kind);
        Assert.True(alert.IsUrgent);
        Assert.Equal("Camera cam", alert.DeviceName);
        Assert.StartsWith("A change to the lock left a camera out, so BlindsDown turned it back on: Camera cam.", alert.Message, StringComparison.Ordinal);

        // Again within the alert cooldown: still never quiet.
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Engine.Lock(new DeviceLockRequest { Cameras = false });

        Assert.Equal(2, world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked).Count);
    }

    [Fact]
    public void ACameraNamedAsHardwareToKeepOnIsTurnedBackOnWithAnAlertAndShownAsAProblem()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Engine.Lock(PnpWorld.CamerasOnly with { KeepEnabledInstanceIds = [world.Id("cam")] });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.Equal(DeviceLockEngine.KeptForMicrophoneProblem, world.DeviceStatus("cam").Problem);
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
    }

    [Fact]
    public void ADeviceTurnedBackOnOnlyWhenItIsListedAgainIsAnnouncedThen()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices["mic"].Kind = DeviceKind.Microphone; // Not listed as microphone hardware for now.

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));

        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Engine.Reconcile();

        Assert.False(world.Devices["mic"].Disabled);
        Assert.Equal("Microphone mic", Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked)).DeviceName);
    }

    [Fact]
    public void ARequestThatLeavesOutACameraInUseIsAnnouncedTooThoughItIsAlreadyOn()
    {
        // The camera was in use when the lock started, so BlindsDown keeps trying to turn it off. A narrower request
        // makes it stop trying, and the lock would look complete: never quietly.
        PnpWorld world = Setup();
        world.Devices["cam"].DisableAlways = PnpChangeResult.Vetoed;
        world.Engine.Lock(PnpWorld.Everything);
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);

        world.Engine.Lock(new DeviceLockRequest { MicHardware = true });

        Assert.False(world.HasAction("cam"));
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.True(alert.IsUrgent);
        Assert.Equal("Camera cam", alert.DeviceName);
        Assert.Equal(
            "A change to the lock left a camera out, so BlindsDown no longer turns it off: Camera cam. If you didn't change the lock, unlock and lock again, and check which programs are running.",
            alert.Message);
    }

    [Fact]
    public void ARequestThatLeavesOutAnUnpluggedCameraIsAnnouncedWithTheOnesItTurnedBackOn()
    {
        // Unplugged, it would have been turned off again the moment it was back; now it comes back on.
        PnpWorld world = Setup();
        world.Devices.Add("cam2");
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.Unplug("cam2");
        world.Pump();

        world.Engine.Lock(new DeviceLockRequest { MicHardware = true });

        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.Equal("Camera cam, Camera cam2", alert.DeviceName);
        Assert.StartsWith(
            "A change to the lock left 2 devices out, so BlindsDown turned Camera cam back on and no longer turns Camera cam2 off.",
            alert.Message,
            StringComparison.Ordinal);

        world.Devices.Plug("cam2");
        world.Pump();
        Assert.False(world.Devices["cam2"].Disabled);
    }

    [Fact]
    public void ANarrowingACrashInterruptedIsAnnouncedWhenItIsFinishedAtStart()
    {
        static PnpWorld LockedWithACameraInUse()
        {
            PnpWorld world = Setup();
            world.Devices["cam"].DisableAlways = PnpChangeResult.Vetoed;
            world.Engine.Lock(PnpWorld.Everything);
            return world;
        }

        // The crash comes right after the new request was recorded, before the camera was taken out of the lock.
        PnpWorld probe = LockedWithACameraInUse();
        probe.Crash.Record();
        probe.Engine.Lock(new DeviceLockRequest { MicHardware = true });
        int afterRequestSaved = probe.Crash.Seen.FindIndex(p => p.StartsWith("after journal save (Locking)", StringComparison.Ordinal)) + 2;

        PnpWorld world = LockedWithACameraInUse();
        world.Crash.ArmAt(afterRequestSaved);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock(new DeviceLockRequest { MicHardware = true }));
        Assert.True(world.HasAction("cam"));

        world.Reboot();

        Assert.False(world.HasAction("cam"));
        Assert.Equal("Camera cam", Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked)).DeviceName);
    }

    [Fact]
    public void AHelperStartThatOnlyReEvaluatesTheSameRequestRaisesNoChangeAlarm()
    {
        // After a restart of the PC every camera is on again. One that Windows now describes as an infrared camera
        // falls out of the same request: nothing about the lock changed, so nothing is announced as a change.
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Devices["cam"].CameraType = CameraType.Infrared;

        world.Reboot();

        Assert.False(world.HasAction("cam"));
        Assert.False(world.Devices["cam"].Disabled);
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void ADeviceTheHelperStartCouldOnlyMatchOnceListedIsNoChangeAlarmEither()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices["mic"].Kind = DeviceKind.Microphone; // Not listed as microphone hardware for now.

        world.Reboot();
        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Devices["mic"].IsVirtual = true; // Now described as software: out of the same request.
        world.Engine.Reconcile();

        Assert.False(world.HasAction("mic"));
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void ANewExclusionTurnsThatCameraBackOn()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("cam")] });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("cam").Protection);
    }

    [Fact]
    public void HardwareTheAppNowNeedsKeptOnIsTurnedBackOn()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        Assert.True(world.Devices["mic"].Disabled);

        world.Engine.Lock(PnpWorld.Everything with { KeepEnabledInstanceIds = [world.Id("mic")] });

        Assert.False(world.Devices["mic"].Disabled);
        Assert.True(world.Devices["cam"].Disabled);
    }

    [Fact]
    public void NarrowingLeavesDevicesTheUserHadOffAlone()
    {
        PnpWorld world = Setup();
        world.Devices.Add("off", disabledByUser: true);
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("off")] });

        Assert.True(world.Devices["off"].Disabled);
        Assert.Equal(0, world.Devices.EnableCallsFor(world.Id("off")));
        Assert.False(world.HasAction("off"));
    }

    [Fact]
    public void ADeviceThatCantBeTurnedBackOnStaysInTheLockAndUnlockTriesAgain()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices["mic"].EnableResults.Enqueue(PnpChangeResult.Failed);

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.True(world.Devices["mic"].Disabled);
        Assert.True(world.HasAction("mic"));
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("mic").Protection);
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.Equal("BlindsDown couldn't turn a microphone back on (simulated Failed). It stays off until you unlock: Microphone mic.", alert.Message);

        world.Engine.Unlock();

        Assert.False(world.Devices["mic"].Disabled);
    }

    [Fact]
    public void ANarrowingThatCantListTheDevicesYetHappensOnceItCan()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.EnumerateThrows = true;

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.True(world.Devices["mic"].Disabled);

        // The tray app sends the same (new) request again: still released once the list can be read.
        world.Devices.EnumerateThrows = false;
        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Devices["mic"].Disabled);
        Assert.False(world.HasAction("mic"));
    }

    [Fact]
    public void ANarrowingACrashInterruptedIsFinishedAtStart()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Crash.Record();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        int beforeEnable = world.Crash.Seen.FindIndex(p => p.StartsWith("before enable", StringComparison.Ordinal)) + 1;

        PnpWorld crashed = Setup();
        crashed.Engine.Lock(PnpWorld.Everything);
        crashed.Crash.ArmAt(beforeEnable);
        Assert.Throws<SimulatedCrashException>(() => crashed.Engine.Lock(PnpWorld.CamerasOnly));
        Assert.True(crashed.Devices["mic"].Disabled);

        crashed.Restart();

        Assert.False(crashed.Devices["mic"].Disabled);
        Assert.True(crashed.Devices["cam"].Disabled);
        Assert.False(crashed.HasAction("mic"));
    }

    [Fact]
    public void ALockedDeviceStaysLockedWhenAnExclusionStartsToFitItWhileLocked()
    {
        var world = new PnpWorld();
        world.Devices.Add("twinA", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinA");
        world.Devices.Add("twinB", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinB");
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("twinA")] });
        Assert.True(world.Devices["twinB"].Disabled);

        // With A gone, the exclusion could now mean B (A on another port). B is in the lock, so it stays off.
        world.Devices.Unplug("twinA");
        world.Pump();
        world.Engine.Reconcile();

        Assert.True(world.Devices["twinB"].Disabled);
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("twinB").Protection);
    }

    [Fact]
    public void ADeviceWindowsDidNotListWhenTheRequestChangedIsMatchedWhenItIsListedAgain()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.Unplug("mic");
        world.Devices["mic"].Kind = DeviceKind.Microphone; // Windows doesn't list it as microphone hardware for now.
        world.Pump();
        int disables = world.Devices.DisableCallsFor(world.Id("mic"));

        // The app now needs that microphone on (e.g. it became the default).
        world.Engine.Lock(PnpWorld.Everything with { KeepEnabledInstanceIds = [world.Id("mic")] });
        Assert.True(world.HasAction("mic")); // Can't be matched against the request yet.

        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Devices.Plug("mic");
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Devices["mic"].Disabled);
        Assert.Equal(disables, world.Devices.DisableCallsFor(world.Id("mic")));
        Assert.False(world.HasAction("mic"));
        Assert.Empty(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        Assert.True(world.Devices["cam"].Disabled);
    }

    [Fact]
    public void ADeviceStillOffButNotListedWhenTheRequestChangedIsTurnedOnOnceListed()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices["mic"].Kind = DeviceKind.Microphone;

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.True(world.Devices["mic"].Disabled);

        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Engine.Reconcile();

        Assert.False(world.Devices["mic"].Disabled);
        Assert.False(world.HasAction("mic"));
        world.Engine.Unlock();
        Assert.False(world.Devices["cam"].Disabled);
    }

    [Fact]
    public void AnUnlistedDeviceIsStillMatchedWhenItReturnsAfterAHelperRestart()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        world.Devices.Unplug("mic");
        world.Devices["mic"].Kind = DeviceKind.Microphone;
        world.Pump();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Restart();
        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Devices.Plug("mic");
        world.Pump();

        Assert.False(world.Devices["mic"].Disabled);
        Assert.False(world.HasAction("mic"));
    }

    [Fact]
    public void AnUnlistedDeviceTheNewRequestStillCoversIsTurnedOffWhenItReturns()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything with { IncludeInfrared = true });
        world.Devices.Unplug("mic");
        world.Devices["mic"].Kind = DeviceKind.Microphone;
        world.Pump();

        world.Engine.Lock(PnpWorld.Everything);
        Assert.False(world.Devices["ir"].Disabled);

        world.Devices["mic"].Kind = DeviceKind.MicrophoneHardware;
        world.Devices.Plug("mic");
        world.Pump();

        Assert.True(world.Devices["mic"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        world.Engine.Unlock();
        Assert.False(world.Devices["mic"].Disabled);
    }

    [Fact]
    public void TheSameRequestInAnotherOrderIsNotAChange()
    {
        PnpWorld world = Setup();
        world.Devices.AddMic("mic2");
        var exclusions = new[] { world.ExclusionFor("ir"), world.ExclusionFor("cam") };
        world.Engine.Lock(PnpWorld.Everything with { Exclusions = exclusions, KeepEnabledInstanceIds = [world.Id("mic"), world.Id("mic2")] });
        int saves = world.MemoryJournal.SaveCount;

        world.Engine.Lock(PnpWorld.Everything with
        {
            Exclusions = [exclusions[1] with { DisplayName = "renamed" }, exclusions[0]],
            KeepEnabledInstanceIds = [world.Id("mic2").ToUpperInvariant(), world.Id("mic")],
        });

        Assert.Equal(saves, world.MemoryJournal.SaveCount);
        Assert.Empty(world.Devices.EnableCalls);
    }

    [Fact]
    public void RequestComparisonCoversEveryOption()
    {
        var baseline = new DeviceLockRequest { Cameras = true };

        Assert.True(baseline.LocksSameAs(baseline with { }));
        Assert.False(baseline.LocksSameAs(baseline with { Cameras = false }));
        Assert.False(baseline.LocksSameAs(baseline with { MicHardware = true }));
        Assert.False(baseline.LocksSameAs(baseline with { IncludeInfrared = true }));
        Assert.False(baseline.LocksSameAs(baseline with { IncludeVirtual = true }));
        Assert.False(baseline.LocksSameAs(baseline with { KeepEnabledInstanceIds = ["x"] }));
        Assert.False(baseline.LocksSameAs(baseline with { Exclusions = [new DeviceExclusion { StableKey = "cam|x|y", DisplayName = "y" }] }));
        Assert.False(
            (baseline with { Exclusions = [new DeviceExclusion { StableKey = "k", LastKnownId = "a", DisplayName = "n" }] })
            .LocksSameAs(baseline with { Exclusions = [new DeviceExclusion { StableKey = "k", LastKnownId = "b", DisplayName = "n" }] }));
    }

    [Fact]
    public void WideningThenNarrowingThenUnlockingRestoresEverythingExactly()
    {
        PnpWorld world = Setup();
        world.Devices.Add("off", disabledByUser: true);
        world.Devices.Add("vcam", type: CameraType.Virtual, isVirtual: true);

        world.Engine.Lock(PnpWorld.CamerasOnly);
        world.Engine.Lock(new DeviceLockRequest { Cameras = true, MicHardware = true, IncludeInfrared = true, IncludeVirtual = true });
        world.Engine.Lock(new DeviceLockRequest { MicHardware = true });
        world.Engine.Lock(PnpWorld.Everything with { IncludeVirtual = true });
        world.Engine.Unlock();

        foreach (FakePnpDevice device in world.Devices.All)
        {
            Assert.Equal(device.Original, (device.Disabled, device.DisabledPersistently));
        }
    }

    private static PnpWorld Setup()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam");
        world.Devices.Add("ir", type: CameraType.Infrared);
        world.Devices.AddMic("mic");
        world.Engine.Initialize();
        return world;
    }
}
