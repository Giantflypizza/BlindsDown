using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>While locked: devices turned back on, unplugged and plugged back in, or newly connected.</summary>
public sealed class DeviceLockEngineGuardTests
{
    [Fact]
    public void ACameraTurnedBackOnWhileLockedIsTurnedOffAgainWithAnAlert()
    {
        PnpWorld world = Locked();

        world.Devices.SomeoneEnables("cam1");
        world.Pump();

        Assert.True(world.Devices["cam1"].Disabled);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.ReEnabledWhileLocked, alert.Kind);
        Assert.Equal("Something turned a camera back on while locked. BlindsDown turned it off again: HD Webcam C270.", alert.Message);
        Assert.True(alert.IsUrgent);
        Assert.Equal(1, world.Action("cam1").GuardCount);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
    }

    [Fact]
    public void AMissedNotificationIsCaughtByTheSafetyPoll()
    {
        PnpWorld world = Locked();

        world.Devices.SomeoneEnables("cam1");
        world.Devices.TakeNotifications(); // Lost.
        world.Engine.Reconcile();

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void SomethingThatKeepsTurningACameraOnRaisesATamperingAlert()
    {
        PnpWorld world = Locked();

        for (int i = 0; i < 5; i++)
        {
            world.Devices.SomeoneEnables("cam1");
            world.Pump();
            world.Clock.AdvanceSeconds(5);
        }

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked)); // Throttled to one per 30 s.
        EngineAlert tamper = Assert.Single(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.Equal("Something keeps turning a camera back on (5 times in the last minute). BlindsDown keeps turning it off; check which app is doing this: HD Webcam C270.", tamper.Message);
        Assert.Equal(5, world.Action("cam1").GuardCount);
    }

    [Fact]
    public void ACameraPluggedBackInComesBackOnAndIsTurnedOffAgain()
    {
        PnpWorld world = Locked();

        world.Devices.Unplug("cam1");
        world.Pump();
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("cam1").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Devices.Plug("cam1");
        Assert.False(world.Devices["cam1"].Disabled); // The disable isn't persistent.
        world.Pump();

        Assert.True(world.Devices["cam1"].Disabled);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.DeviceRelocked, alert.Kind);
        Assert.Equal("A camera was plugged back in while locked and was on for a moment. BlindsDown turned it off again: HD Webcam C270.", alert.Message);
        Assert.False(alert.IsUrgent);
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("cam1").Protection);

        world.Engine.Unlock();
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ACameraPluggedBackInThatCantBeTurnedOffIsAnUrgentAlert()
    {
        PnpWorld world = Locked();
        world.Devices.Unplug("cam1");
        world.Pump();
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;

        world.Devices.Plug("cam1");
        world.Pump();

        EngineAlert relocked = Assert.Single(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        Assert.True(relocked.IsUrgent);
        Assert.Equal("A camera was plugged back in while locked, and BlindsDown couldn't turn it off: HD Webcam C270. In use by an app; BlindsDown keeps trying.", relocked.Message);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeviceBusy));
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
    }

    [Fact]
    public void ACameraTurnedBackOnThatCantBeTurnedOffAgainSaysSo()
    {
        PnpWorld world = Locked();
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;

        world.Devices.SomeoneEnables("cam1");
        world.Pump();

        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.Equal("Something turned a camera back on while locked, and BlindsDown couldn't turn it off again: HD Webcam C270. In use by an app; BlindsDown keeps trying.", alert.Message);
        Assert.Equal("In use by an app; BlindsDown keeps trying.", world.DeviceStatus("cam1").Problem);

        world.Devices["cam1"].DisableAlways = null;
        world.Engine.Reconcile();
        Assert.True(world.Devices["cam1"].Disabled);

        world.Engine.Unlock();
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ANewCameraConnectedWhileLockedIsTurnedOffWithAnAlert()
    {
        PnpWorld world = Locked();

        world.Devices.Add("new", name: "Logitech BRIO");
        world.Devices.Plug("new");
        world.Pump();

        Assert.True(world.Devices["new"].Disabled);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.NewDeviceLocked, alert.Kind);
        Assert.Equal("A new camera was connected while locked. BlindsDown turned it off: Logitech BRIO.", alert.Message);
        Assert.False(alert.IsUrgent);
        Assert.True(world.Action("new").AddedWhileLocked);

        world.Engine.Unlock();
        Assert.False(world.Devices["new"].Disabled);
    }

    [Fact]
    public void NewMicrophoneHardwareIsNamedAsAMicrophone()
    {
        PnpWorld world = Locked(PnpWorld.Everything);

        world.Devices.AddMic("usbmic");
        world.Devices.Plug("usbmic");
        world.Pump();

        Assert.Equal("A new microphone was connected while locked. BlindsDown turned it off: Microphone usbmic.", Assert.Single(world.Alerts).Message);
    }

    [Fact]
    public void ANewDeviceThatIsAlreadyOffIsRecordedWithoutAnAlertAndLeftOff()
    {
        PnpWorld world = Locked();

        world.Devices.Add("new", disabledByUser: true);
        world.Devices.Plug("new");
        world.Pump();

        Assert.Empty(world.Alerts);
        Assert.True(world.Action("new").WasAlreadyLocked);
        world.Engine.Unlock();
        Assert.True(world.Devices["new"].Disabled);
        Assert.Equal(0, world.Devices.EnableCallsFor(world.Id("new")));
    }

    [Fact]
    public void NewDevicesOutsideTheRequestAreLeftAlone()
    {
        PnpWorld world = Locked(request: PnpWorld.CamerasOnly with { Exclusions = [] });
        world.Devices.Add("ir", type: CameraType.Infrared);
        world.Devices.AddMic("mic");
        world.Devices.Plug("ir");
        world.Devices.Plug("mic");

        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Devices["ir"].Disabled);
        Assert.False(world.Devices["mic"].Disabled);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void ANewExcludedDeviceIsLeftAlone()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("later", present: false);
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("later")] });

        world.Devices.Plug("later");
        world.Pump();

        Assert.False(world.Devices["later"].Disabled);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("later").Protection);
    }

    [Fact]
    public void ADeviceTheUserHadOffAndTurnsOnWhileLockedIsTurnedOffAgainAndLeftOffAtUnlock()
    {
        var world = new PnpWorld();
        world.Devices.Add("off", disabledByUser: true);
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        world.Devices.SomeoneEnables("off");
        world.Pump();
        Assert.True(world.Devices["off"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));

        RestoreReport report = world.Engine.Unlock()!;

        // It was off before the lock, so it is off after it.
        Assert.True(world.Devices["off"].Disabled);
        Assert.Equal("Off, as before the lock.", Assert.Single(report.Lines).Message);
    }

    [Fact]
    public void ADeviceTurnedOffByOthersAfterAFailedDisableIsOnAgainAfterUnlock()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);

        // Off now, whoever did it. It was on before the lock, so unlock turns it on.
        world.Devices["cam1"].Disabled = true;
        world.Engine.Reconcile();
        Assert.Equal(DeviceProtection.DeviceDisabled, world.DeviceStatus("cam1").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Engine.Unlock();

        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void TheEngineCausesNoNotificationStorm()
    {
        PnpWorld world = Locked();
        world.Devices["cam1"].DisableAlways = PnpChangeResult.NeedsRestart;
        world.Devices.SomeoneEnables("cam1");

        world.Pump(); // Throws on a storm.

        Assert.Equal(2, world.Devices.DisableCallsFor(world.Id("cam1")));
    }

    [Fact]
    public void GuardingSavesTheJournalOnlyOnChanges()
    {
        PnpWorld world = Locked();
        int saves = world.MemoryJournal.SaveCount;

        for (int i = 0; i < 10; i++)
        {
            world.Engine.Reconcile();
            world.Engine.OnDevicesChanged();
        }

        Assert.Equal(saves, world.MemoryJournal.SaveCount);
    }

    [Fact]
    public void AVetoedDeviceIsRetriedWithoutWritingTheJournalEachTime()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;
        world.Engine.Initialize();
        world.Engine.Lock(PnpWorld.CamerasOnly);
        int saves = world.MemoryJournal.SaveCount;

        for (int i = 0; i < 10; i++)
        {
            world.Engine.Reconcile();
        }

        Assert.Equal(11, world.Devices.DisableCallsFor(world.Id("cam1")));
        Assert.Equal(saves, world.MemoryJournal.SaveCount);
    }

    private static PnpWorld Locked(DeviceLockRequest? request = null)
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1", name: "HD Webcam C270");
        world.Engine.Initialize();
        world.Engine.Lock(request ?? PnpWorld.CamerasOnly);
        world.Pump();
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Empty(world.Alerts);
        return world;
    }
}
