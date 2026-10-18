using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>Service restarts, PC restarts and damaged journals.</summary>
public sealed class DeviceLockEngineRecoveryTests
{
    [Fact]
    public void AfterARestartTheLockIsReappliedQuietly()
    {
        PnpWorld world = Locked();

        Assert.True(world.Reboot());

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.True(world.Devices["mic1"].Disabled);
        Assert.True(world.Devices["off"].Disabled);
        Assert.Equal(0, world.Devices.DisableCallsFor(world.Id("off")));
        Assert.Empty(world.Alerts);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.True(world.Engine.HasLock);

        world.Engine.Unlock();
        AssertAsBefore(world);
    }

    [Fact]
    public void ADeviceConnectedWhileThePcWasOffIsLockedQuietlyAtStart()
    {
        PnpWorld world = Locked();
        world.Devices.Add("new");

        world.Reboot();

        Assert.True(world.Devices["new"].Disabled);
        Assert.Empty(world.Alerts);
        Assert.True(world.Action("new").AddedWhileLocked);
    }

    [Fact]
    public void ALockedDeviceThatIsAwayAtStartIsRelockedWhenItArrives()
    {
        PnpWorld world = Locked();
        world.Devices.Unplug("cam1");
        world.Reboot();
        Assert.Empty(world.Alerts);

        world.Devices.Plug("cam1");
        world.Pump();

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeviceRelocked));
    }

    [Fact]
    public void ARestartOfTheHelperAloneFindsEverythingStillLocked()
    {
        PnpWorld world = Locked();
        int disables = world.Devices.DisableCalls.Count;

        Assert.True(world.Restart());

        Assert.Equal(disables, world.Devices.DisableCalls.Count);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        world.Engine.Unlock();
        AssertAsBefore(world);
    }

    [Fact]
    public void AnUnreadableJournalChangesNothingAndSaysToRestart()
    {
        PnpWorld world = Locked();
        world.MemoryJournal.Corrupt = true;
        int disables = world.Devices.DisableCalls.Count;

        Assert.False(world.Restart());

        Assert.True(world.Engine.JournalWasCorrupt);
        Assert.False(world.Engine.HasLock);
        Assert.Equal(disables, world.Devices.DisableCalls.Count);
        Assert.Empty(world.Devices.EnableCalls);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
        Assert.Contains("BlindsDown's record of the camera lock could not be read. Restart the PC to turn every camera back on.", world.Engine.Status.Problems);
        Assert.Null(world.Engine.Unlock());

        // The restart it asks for brings every device back.
        world.Devices.Reboot();
        AssertAsBefore(world);
    }

    [Fact]
    public void AnInterruptedUnlockIsFinishedAtStart()
    {
        PnpWorld world = Locked();
        world.Crash.ArmAt(PnpWorld.CountCrashPoints(Locked, w => w.Engine.Unlock()) / 2);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());

        Assert.False(world.Restart());

        Assert.Null(world.PersistedJournal);
        Assert.False(world.Engine.HasLock);
        AssertAsBefore(world);
    }

    [Fact]
    public void AnInterruptedLockIsFinishedAtStart()
    {
        PnpWorld world = Setup();
        world.Crash.ArmAt(PnpWorld.CountCrashPoints(Setup, w => w.Engine.Lock(PnpWorld.Everything)) / 2);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock(PnpWorld.Everything));
        Assert.Equal(LockPhase.Locking, world.PersistedJournal!.Phase);

        Assert.True(world.Restart());

        Assert.Equal(LockPhase.Locked, world.PersistedJournal!.Phase);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.True(world.Devices["mic1"].Disabled);
        world.Engine.Unlock();
        AssertAsBefore(world);
    }

    [Fact]
    public void LockingBeforeInitializeTakesOverTheLockOnDisk()
    {
        PnpWorld world = Locked();
        world.Devices.Reboot();
        DeviceLockEngine fresh = world.CreateEngine();

        fresh.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.PersistedJournal!.Actions.Exists(a => a.TargetId == world.Id("cam1") && a.WasAlreadyLocked));
        fresh.Unlock();
        AssertAsBefore(world);
    }

    [Fact]
    public void UnlockingBeforeInitializeDoesNotReapplyTheLockFirst()
    {
        PnpWorld world = Locked();
        world.Devices.Reboot();
        int disables = world.Devices.DisableCalls.Count;
        DeviceLockEngine fresh = world.CreateEngine();

        RestoreReport report = fresh.Unlock()!;

        Assert.Equal(disables, world.Devices.DisableCalls.Count);
        Assert.True(report.AllExact);
        Assert.Null(world.PersistedJournal);
        AssertAsBefore(world);
    }

    [Fact]
    public void ASafetyPollBeforeInitializeInitializes()
    {
        PnpWorld world = Locked();
        world.Devices.Reboot();
        DeviceLockEngine fresh = world.CreateEngine();

        fresh.Reconcile();

        Assert.True(fresh.HasLock);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void LockingAgainAfterAnInterruptedUnlockKeepsTheOriginalStates()
    {
        PnpWorld world = Locked();
        world.Crash.ArmAt(PnpWorld.CountCrashPoints(Locked, w => w.Engine.Unlock()) / 2);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());

        // The same engine (it survived, e.g. an unexpected error) is asked to lock again.
        world.Crash.Disarm();
        world.Engine.Lock(PnpWorld.Everything);

        Assert.True(world.Devices["cam1"].Disabled);
        Assert.True(world.Devices["mic1"].Disabled);
        Assert.True(world.Action("off").WasAlreadyLocked);
        world.Engine.Unlock();
        AssertAsBefore(world);
    }

    [Fact]
    public void LockingLessAfterAnInterruptedUnlockTurnsTheRestBackOnWithoutAnAlarm()
    {
        PnpWorld probe = Locked();
        probe.Crash.Record();
        probe.Engine.Unlock();
        int afterUnlockingSaved = probe.Crash.Seen.FindIndex(p => p.StartsWith("after journal save (Unlocking)", StringComparison.Ordinal)) + 1;

        PnpWorld world = Locked();
        world.Crash.ArmAt(afterUnlockingSaved);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        world.Crash.Disarm();

        // Those devices were being turned back on anyway: an unlock was under way.
        world.Engine.Lock(PnpWorld.CamerasOnly);

        Assert.False(world.Devices["mic1"].Disabled);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
    }

    [Fact]
    public void ADamagedJournalEntryIsNeverActedOn()
    {
        PnpWorld world = Locked();
        DeviceLockJournal journal = world.PersistedJournal!;
        journal.Actions.Add(new LockAction { Kind = ActionKind.MuteMic, TargetId = "{0.0.1.00000000}.{guid}", Status = ActionStatus.Applied });
        journal.Actions.Add(new LockAction { Kind = ActionKind.DisableDevice, TargetId = string.Empty, Status = ActionStatus.Applied });
        world.MemoryJournal.Save(journal);

        world.Restart();
        world.Engine.Unlock();

        Assert.DoesNotContain("{0.0.1.00000000}.{guid}", world.Devices.EnableCalls);
        Assert.DoesNotContain(string.Empty, world.Devices.EnableCalls);
    }

    private static PnpWorld Setup()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("off", disabledByUser: true);
        world.Devices.AddMic("mic1");
        world.Engine.Initialize();
        return world;
    }

    private static PnpWorld Locked()
    {
        PnpWorld world = Setup();
        world.Engine.Lock(PnpWorld.Everything);
        Assert.True(world.Devices["cam1"].Disabled);
        return world;
    }

    private static void AssertAsBefore(PnpWorld world)
    {
        foreach (FakePnpDevice device in world.Devices.All)
        {
            Assert.True(device.Original == (device.Disabled, device.DisabledPersistently), $"{device} is not as before the lock");
        }
    }
}
