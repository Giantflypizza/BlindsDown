using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>Which devices a request covers: the helper picks them itself, and every option can only make it do less
/// than "all colour cameras" or more only when the user asked for it.</summary>
public sealed class DeviceLockEngineSelectionTests
{
    [Theory]
    [InlineData(CameraType.Color, false, false, true)]
    [InlineData(CameraType.ColorAndInfrared, false, false, true)]
    [InlineData(CameraType.Infrared, false, false, false)]
    [InlineData(CameraType.Infrared, true, false, true)]
    [InlineData(CameraType.Virtual, false, false, false)]
    [InlineData(CameraType.Virtual, false, true, true)]
    [InlineData(CameraType.Infrared, false, true, false)]
    [InlineData(CameraType.Virtual, true, false, false)]
    public void CameraTypesAreLockedOnlyWhenRequested(CameraType type, bool includeInfrared, bool includeVirtual, bool locked)
    {
        var world = new PnpWorld();
        world.Devices.Add("cam", type: type, isVirtual: type == CameraType.Virtual);
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(new DeviceLockRequest { Cameras = true, IncludeInfrared = includeInfrared, IncludeVirtual = includeVirtual });

        Assert.Equal(locked, world.Devices["cam"].Disabled);
        Assert.Equal(locked, world.HasAction("cam"));
        Assert.Equal(LockState.Locked, status.State);
        DeviceLockInfo info = world.DeviceStatus("cam");
        Assert.Equal(locked ? DeviceProtection.DeviceDisabled : DeviceProtection.None, info.Protection);
        Assert.Null(info.Problem);
        Assert.Equal(type, info.Camera);
    }

    [Fact]
    public void ACameraWithoutATypeCountsAsColour()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam", type: null);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.True(world.Devices["cam"].Disabled);
        Assert.Equal(CameraType.Color, world.DeviceStatus("cam").Camera);
    }

    [Fact]
    public void ASoftwareDeviceIsVirtualWhateverTypeItReports()
    {
        var world = new PnpWorld();
        world.Devices.Add("vcam", type: CameraType.Color, isVirtual: true);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Devices["vcam"].Disabled);
        Assert.Equal(CameraType.Virtual, world.DeviceStatus("vcam").Camera);
    }

    [Fact]
    public void AnUnknownCameraTypeIsLockedLikeColour()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam", type: (CameraType)42);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.True(world.Devices["cam"].Disabled);
        Assert.Equal(CameraType.Color, world.DeviceStatus("cam").Camera); // Never an undefined value on the wire.
    }

    [Fact]
    public void MicrophoneHardwareIsLockedOnlyWhenRequestedAndNeverWhenVirtual()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam");
        world.Devices.AddMic("mic");
        world.Devices.AddMic("vmic", isVirtual: true, instanceId: @"ROOT\MEDIA\vmic");
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly);
        Assert.False(world.Devices["mic"].Disabled);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("mic").Protection);
        world.Engine.Unlock();

        world.Engine.Lock(new DeviceLockRequest { MicHardware = true });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.True(world.Devices["mic"].Disabled);
        Assert.False(world.Devices["vmic"].Disabled);
        Assert.DoesNotContain(world.Devices.DisableCalls, c => c.StartsWith("ROOT", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepEnabledDevicesAreNeverTouched()
    {
        var world = new PnpWorld();
        world.Devices.AddMic("mic1");
        world.Devices.AddMic("mic2");
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.Everything with { KeepEnabledInstanceIds = [world.Id("mic1").ToLowerInvariant()] });

        Assert.False(world.Devices["mic1"].Disabled);
        Assert.True(world.Devices["mic2"].Disabled);
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("mic1")));
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("mic1").Protection);
        Assert.Null(world.DeviceStatus("mic1").Problem);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        // Also while locked: a kept device that is turned off and on again is still left alone.
        world.Devices.Unplug("mic1");
        world.Devices.Plug("mic1");
        world.Pump();
        world.Engine.Reconcile();
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("mic1")));
    }

    [Fact]
    public void ACameraTheAppNeedsKeptOnIsLeftOnButNeverLooksLocked()
    {
        // One device that is both a camera and the hardware of a default or excluded microphone (a capture card):
        // turning it off would take that microphone away, so it stays on, and the lock says why.
        var world = new PnpWorld();
        world.Devices.Add("card");
        world.Devices.Add("cam2");
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(PnpWorld.Everything with { KeepEnabledInstanceIds = [world.Id("card")] });

        Assert.False(world.Devices["card"].Disabled);
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("card")));
        Assert.True(world.Devices["cam2"].Disabled);
        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("card").Protection);
        Assert.Equal(DeviceLockEngine.KeptForMicrophoneProblem, world.DeviceStatus("card").Problem);
        Assert.Empty(world.Alerts); // Never turned off, so nothing was turned back on.
    }

    [Fact]
    public void AnExcludedCameraIsLeftOnAndShownAsExcluded()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("cam2");
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("cam2")] });

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.False(world.Devices["cam2"].Disabled);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("cam2").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("cam2")));
    }

    [Fact]
    public void AnExclusionFollowsACameraToAnotherUsbPort()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        DeviceExclusion exclusion = world.ExclusionFor("cam1");
        world.Devices.MoveToPort("cam1", "cam1b");
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [exclusion] });

        Assert.False(world.Devices["cam1b"].Disabled);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("cam1b").Protection);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("cam1").Protection);
        Assert.Empty(world.Devices.DisableCalls);
    }

    [Fact]
    public void ExcludingOneOfTwoIdenticalCamerasNeverExcludesTheOther()
    {
        var world = new PnpWorld();
        world.Devices.Add("twinA", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinA");
        world.Devices.Add("twinB", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinB");
        Assert.Equal(world.Devices["twinA"].ToPnpDevice().StableKey, world.Devices["twinB"].ToPnpDevice().StableKey);
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [world.ExclusionFor("twinA")] });

        Assert.False(world.Devices["twinA"].Disabled);
        Assert.True(world.Devices["twinB"].Disabled);
    }

    [Fact]
    public void AfterAPortChangeAnExclusionThatCouldMeanEitherTwinExcludesNeither()
    {
        var world = new PnpWorld();
        world.Devices.Add("twinA", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinA");
        world.Devices.Add("twinB", name: "USB Camera", instanceId: @"USB\VID_1234&PID_5678&MI_00\twinB");
        DeviceExclusion exclusion = world.ExclusionFor("twinA");
        world.Devices.MoveToPort("twinA", "twinA2");
        world.Engine.Initialize();

        world.Engine.Lock(PnpWorld.CamerasOnly with { Exclusions = [exclusion] });

        // Privacy wins when BlindsDown can't tell which twin the user meant.
        Assert.True(world.Devices["twinA2"].Disabled);
        Assert.True(world.Devices["twinB"].Disabled);
    }

    [Fact]
    public void AnExclusionOnlyMatchesDevicesOfItsOwnKind()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam", name: "Webcam", instanceId: @"USB\VID_3456&PID_789A&MI_00\cam");
        world.Devices.AddMic("mic", instanceId: @"USB\VID_3456&PID_789A&MI_02\mic");
        world.Engine.Initialize();

        // The webcam's camera excluded by stable key only: its microphone function is a different device.
        world.Engine.Lock(PnpWorld.Everything with { Exclusions = [world.ExclusionFor("cam", byStableKeyOnly: true)] });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.True(world.Devices["mic"].Disabled);
    }

    [Fact]
    public void ExclusionTwinsAreCountedAmongConnectedDevicesOfTheSameKindOnly()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam", name: "Gadget", instanceId: @"USB\VID_1111&PID_2222&MI_00\cam");
        world.Devices.AddMic("mic", instanceId: @"USB\VID_1111&PID_2222&MI_00\mic");
        world.Devices["mic"].Name = "Gadget";
        PnpDevice camera = world.Devices["cam"].ToPnpDevice();
        PnpDevice microphone = world.Devices["mic"].ToPnpDevice();
        var exclusion = new DeviceExclusion { StableKey = camera.StableKey, LastKnownId = @"USB\VID_1111&PID_2222&MI_00\gone", DisplayName = "Gadget" };
        DeviceLockRequest request = PnpWorld.Everything with { Exclusions = [exclusion] };

        // Same hardware part and name, but the microphone's key says "hw": it is never the camera's twin.
        Assert.True(DeviceLockEngine.IsExcluded(camera, request, [camera, microphone]));
        Assert.False(DeviceLockEngine.IsExcluded(microphone, request, [camera, microphone]));
    }

    [Fact]
    public void CamerasCanBeLeftOutWhileMicrophoneHardwareIsLocked()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam");
        world.Devices.AddMic("mic");
        world.Engine.Initialize();

        world.Engine.Lock(new DeviceLockRequest { Cameras = false, MicHardware = true });

        Assert.False(world.Devices["cam"].Disabled);
        Assert.True(world.Devices["mic"].Disabled);
    }

    [Fact]
    public void AnEmptyRequestLocksNothingButIsStillALock()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam");
        world.Engine.Initialize();

        DeviceLockStatus status = world.Engine.Lock(new DeviceLockRequest());

        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(world.Devices.DisableCalls);
        RestoreReport report = world.Engine.Unlock()!;
        Assert.Equal(0, report.DeviceCount);
        Assert.Equal("Nothing needed restoring.", report.Summary);
    }

    [Fact]
    public void ARequestWithMissingListsIsTreatedAsEmpty()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam");
        world.Engine.Initialize();

        world.Engine.Lock(new DeviceLockRequest { Cameras = true, Exclusions = null!, KeepEnabledInstanceIds = [null!, string.Empty] });

        Assert.True(world.Devices["cam"].Disabled);
    }
}
