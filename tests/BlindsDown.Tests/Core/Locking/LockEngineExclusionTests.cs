using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// Identical twins: two webcams of the same model have microphones with the same stable key. Excluding one must
/// never let the other go, also not when the excluded one is unplugged and the exclusion could seem to have moved.
/// </summary>
public sealed class LockEngineExclusionTests
{
    [Fact]
    public void TwinOfAnExcludedMicStaysLockedWhenTheExcludedOneIsUnplugged()
    {
        LockWorld world = TwinWorld();
        world.Exclude("cam-a");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.False(world.Mics["cam-a"].Muted);
        Assert.True(world.Mics["cam-b"].Muted);

        // Webcam A is unplugged, and an app unmutes B.
        world.Mics.Unplug("cam-a");
        world.Pump();
        world.Mics.OtherAppSetsMute("cam-b", false);
        world.Pump();
        world.Engine.Reconcile();

        Assert.True(world.Mics["cam-b"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("cam-b").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.DoesNotContain(world.Mics["cam-b"].HardwareInstanceId!, world.Engine.HardwareToKeepEnabled());

        world.Engine.Unlock();
        Assert.False(world.Mics["cam-b"].Muted);
    }

    [Fact]
    public void TwinOfAnExcludedMicStaysTurnedOffWhenTheExcludedOneIsUnplugged()
    {
        LockWorld world = TwinWorld();
        world.Settings.DisableMicsInWindows = true;
        world.Exclude("cam-a");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.True(world.Mics["cam-b"].IsOff);

        // Webcam A is unplugged, and something turns B back on.
        world.Mics.Unplug("cam-a");
        world.Mics.Enable("cam-b");
        world.Pump();

        Assert.True(world.Mics["cam-b"].IsOff && world.Mics["cam-b"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void ExclusionThatKnowsItsTwinIsNotTakenForItWhenTheExcludedMicIsUnpluggedBeforeTheLock()
    {
        LockWorld world = TwinWorld();
        MicEndpoint a = world.Mics["cam-a"].ToEndpoint();
        world.Settings.ExcludedDevices.Add(new DeviceExclusion
        {
            StableKey = a.StableKey,
            LastKnownId = a.Id,
            DisplayName = a.Name,
            TwinIds = ["cam-b"],
        });
        world.Mics.Unplug("cam-a");
        world.Engine.Initialize();

        world.Engine.Lock();

        Assert.True(world.Mics["cam-b"].Muted);
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("cam-b").Protection);
    }

    [Fact]
    public void ExclusionTheUserMakesDuringTheLockStillLetsTheMicGo()
    {
        LockWorld world = TwinWorld();
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Exclude("cam-b");
        world.Mics.OtherAppSetsMute("cam-b", false);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["cam-b"].Muted);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("cam-b").Protection);
        Assert.True(world.Mics["cam-a"].Muted);
    }

    /// <summary>Two identical webcams, "cam-a" and "cam-b" (Windows numbers the second one).</summary>
    private static LockWorld TwinWorld()
    {
        var world = new LockWorld();
        world.Mics.Add("cam-a", hardware: @"USB\VID_1234&PID_0825&MI_02\6&AAAA&0&0002").Name = "Microphone (HD Webcam C270)";
        world.Mics.Add("cam-b", hardware: @"USB\VID_1234&PID_0825&MI_02\6&BBBB&0&0002").Name = "Microphone (2- HD Webcam C270)";
        Assert.Equal(world.Mics["cam-a"].ToEndpoint().StableKey, world.Mics["cam-b"].ToEndpoint().StableKey);
        return world;
    }
}
