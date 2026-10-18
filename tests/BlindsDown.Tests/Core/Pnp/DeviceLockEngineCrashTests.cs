using System.Globalization;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>
/// Kills the helper at every single crash point (before and after every journal write and every device change)
/// of a lock, an unlock, a guard pass or a change of request, then starts a new engine over what was persisted
/// and recovers: the helper restarted, the PC restarted (every non-persistent disable gone), or the lock was kept
/// and unlocked later. Every device must end exactly as it was before the lock. The recovery itself is also
/// killed at every point.
/// </summary>
public sealed class DeviceLockEngineCrashTests
{
    /// <summary>Devices the lock must never change, whatever happens.</summary>
    private static readonly string[] NeverTouched = ["ir", "vcam", "off", "away", "excluded", "kept"];

    public enum Recovery
    {
        /// <summary>The helper service restarted; then the user unlocked.</summary>
        Restart,

        /// <summary>The PC restarted (every device BlindsDown turned off is on again); then the user unlocked.</summary>
        Reboot,

        /// <summary>The helper restarted, the tray app sent its lock again, and later the user unlocked.</summary>
        StayLockedThenUnlock,
    }

    [Theory]
    [InlineData(Recovery.Restart)]
    [InlineData(Recovery.Reboot)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringLockRestoresEveryDeviceExactly(Recovery recovery) =>
        CrashEverywhere(SetupUnlocked, w => w.Engine.Lock(Request(w)), recovery);

    [Theory]
    [InlineData(Recovery.Restart)]
    [InlineData(Recovery.Reboot)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringUnlockRestoresEveryDeviceExactly(Recovery recovery) =>
        CrashEverywhere(SetupLocked, w => w.Engine.Unlock(), recovery);

    [Theory]
    [InlineData(Recovery.Restart)]
    [InlineData(Recovery.Reboot)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointWhileGuardingRestoresEveryDeviceExactly(Recovery recovery) =>
        CrashEverywhere(
            SetupLocked,
            w =>
            {
                // A busy lock: a camera turned back on, one unplugged, one plugged in for the first time.
                w.Devices.SomeoneEnables("cam1");
                w.Devices.Unplug("cam2");
                w.Devices.Plug("late");
                w.Pump();
                w.Engine.Reconcile();
            },
            recovery);

    [Theory]
    [InlineData(Recovery.Restart)]
    [InlineData(Recovery.Reboot)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointWhileNarrowingRestoresEveryDeviceExactly(Recovery recovery) =>
        CrashEverywhere(
            () =>
            {
                PnpWorld world = SetupUnlocked();
                world.Engine.Lock(Request(world) with { IncludeInfrared = true });
                Assert.True(world.Devices["ir"].Disabled);
                return world;
            },
            w => w.Engine.Lock(Request(w) with { MicHardware = false }),
            recovery,
            touched: ["ir"]);

    [Fact]
    public void CrashAtAnyPointDuringLockWithRealFilesRestoresEveryDeviceExactly()
    {
        using var temp = new TempDirectory();
        int n = 1;
        for (; ; n++)
        {
            string name = n.ToString(CultureInfo.InvariantCulture);
            PnpWorld world = SetupUnlocked(new FileDeviceJournalStore(Path.Combine(temp.Path, name, "device-lock-journal.json")));
            world.Crash.ArmAt(n);
            try
            {
                world.Engine.Lock(Request(world));
            }
            catch (SimulatedCrashException)
            {
                string context = $"crash at point {n} ({world.Crash.LastCrash})";
                Recover(world, Recovery.Reboot, crashAt: 0);
                AssertRestored(world, context, []);
                continue;
            }

            world.Crash.Disarm();
            world.Engine.Unlock();
            AssertRestored(world, "no crash", []);
            break;
        }

        Assert.True(n > 20, $"Only {n} crash points.");
    }

    /// <summary>
    /// For every crash point N of <paramref name="operation"/>: crash there, recover; and for every crash point M
    /// of that recovery: crash there too, and recover again.
    /// </summary>
    private static void CrashEverywhere(Func<PnpWorld> setup, Action<PnpWorld> operation, Recovery recovery, string[]? touched = null)
    {
        string[] exempt = touched ?? [];
        int runs = 0;
        for (int n = 1; ; n++)
        {
            bool crashedAtN = false;
            for (int m = 0; ; m++)
            {
                PnpWorld world = setup();
                world.Crash.ArmAt(n);
                try
                {
                    operation(world);
                }
                catch (SimulatedCrashException)
                {
                    crashedAtN = true;
                }

                if (!crashedAtN)
                {
                    world.Crash.Disarm();
                    world.Engine.Unlock();
                    AssertRestored(world, "no crash", exempt);
                    break; // N is past the last crash point of the operation.
                }

                string context = $"{recovery}: crash at point {n} ({world.Crash.LastCrash})";
                bool crashedAtM = false;
                try
                {
                    Recover(world, recovery, crashAt: m);
                    world.Crash.Disarm();
                }
                catch (SimulatedCrashException)
                {
                    crashedAtM = true;
                    context += $", then at recovery point {m} ({world.Crash.LastCrash})";
                    Recover(world, recovery, crashAt: 0);
                }

                AssertRestored(world, context, exempt);
                runs++;
                if (m > 0 && !crashedAtM)
                {
                    break; // M is past the last crash point of the recovery.
                }
            }

            if (!crashedAtN)
            {
                break;
            }
        }

        Assert.True(runs > 50, $"Only {runs} crash scenarios ran; the fake is not exercising the engine.");
    }

    private static void Recover(PnpWorld world, Recovery recovery, int crashAt)
    {
        bool locked = recovery == Recovery.Reboot ? world.Reboot(crashAt) : world.Restart(crashAt);
        if (recovery == Recovery.StayLockedThenUnlock && locked)
        {
            world.Engine.Lock(Request(world));
            foreach (string id in new[] { "cam1", "cam2", "mic1", "late" })
            {
                FakePnpDevice device = world.Devices[id];
                Assert.True(device.Disabled || !device.IsPresent, $"{device} must be off while locked");
            }

            Assert.False(world.Devices["busy"].Disabled);
            Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State); // "busy" is in use.
        }

        world.Engine.Unlock();
    }

    private static void AssertRestored(PnpWorld world, string context, string[] exempt)
    {
        Assert.False(world.Engine.HasLock, context);
        Assert.Null(world.Journal.Load());

        foreach (FakePnpDevice device in world.Devices.All.Where(d => d.IsPresent))
        {
            Assert.True(device.Original == (device.Disabled, device.DisabledPersistently), $"{device} is not as before the lock after {context}");
        }

        foreach (string id in NeverTouched.Except(exempt))
        {
            string instanceId = world.Id(id);
            Assert.True(world.Devices.DisableCallsFor(instanceId) == 0 && world.Devices.EnableCallsFor(instanceId) == 0, $"{id} was touched after {context}");
        }

        // Devices that are away come back as they were before the lock (the disable is not persistent).
        foreach (FakePnpDevice device in world.Devices.All.Where(d => !d.IsPresent).ToList())
        {
            world.Devices.Plug(device.InstanceId);
        }

        world.Pump();
        world.Engine.Reconcile();
        foreach (FakePnpDevice device in world.Devices.All)
        {
            Assert.True(device.Original == (device.Disabled, device.DisabledPersistently), $"{device} is not as before the lock after {context} and replug");
        }
    }

    private static DeviceLockRequest Request(PnpWorld world) => new()
    {
        Cameras = true,
        MicHardware = true,
        Exclusions = [world.ExclusionFor("excluded")],
        KeepEnabledInstanceIds = [world.Id("kept")],
    };

    private static PnpWorld SetupUnlocked() => SetupUnlocked(null);

    private static PnpWorld SetupUnlocked(IDeviceJournalStore? journal)
    {
        var world = new PnpWorld();
        if (journal is not null)
        {
            world.UseJournal(journal);
        }

        FakePnpBackend devices = world.Devices;
        devices.Add("cam1");
        devices.Add("cam2", type: CameraType.ColorAndInfrared);
        devices.Add("ir", type: CameraType.Infrared);
        devices.Add("vcam", type: CameraType.Virtual, isVirtual: true);
        devices.Add("off", disabledByUser: true);
        devices.Add("away", present: false);
        devices.Add("excluded");
        devices.AddMic("mic1");
        devices.AddMic("kept");
        devices.Add("busy").DisableAlways = PnpChangeResult.Vetoed;
        devices.Add("late", present: false);
        world.Engine.Initialize();
        return world;
    }

    private static PnpWorld SetupLocked()
    {
        PnpWorld world = SetupUnlocked();
        world.Engine.Lock(Request(world));
        world.Pump();
        Assert.True(world.Devices["cam1"].Disabled);
        return world;
    }
}
