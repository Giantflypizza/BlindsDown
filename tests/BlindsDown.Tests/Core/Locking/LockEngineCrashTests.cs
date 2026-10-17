using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// Kills the engine at every single crash point (before and after every journal write, pending-store
/// write and device change) of a lock or an unlock, then starts a new engine over what was persisted
/// and recovers the way the UI offers (restore now, or stay locked and unlock later). Every device
/// must end exactly as it was before the lock. The recovery itself is also killed at every point.
/// </summary>
public sealed class LockEngineCrashTests
{
    /// <summary>Mute state before the lock, which is what every device must end with.</summary>
    private static readonly Dictionary<string, bool> Original = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = false, // default mic
        ["b"] = true, // already muted: never touched
        ["c"] = false, // disabled by the user: never touched
        ["d"] = false, // not present: never touched
        ["e"] = false, // excluded: never touched
        ["f"] = false, // jack, unplugged but readable
        ["g"] = true, // Bluetooth hands-free, unreadable: muted, but nobody knows at lock time
        ["h"] = false, // still muted by an earlier lock (pending restore), present
        ["i"] = false, // still muted by an earlier lock (pending restore), absent
        ["j"] = false, // hardware-mute capable communications default
    };

    private static readonly string[] NeverTouched = ["b", "c", "d", "e", "g"];

    public enum Recovery
    {
        /// <summary>"Restore devices" at the startup prompt.</summary>
        UnlockNow,

        /// <summary>"Stay locked", then a normal unlock later.</summary>
        StayLockedThenUnlock,
    }

    [Theory]
    [InlineData(Recovery.UnlockNow)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringLockRestoresEverythingExactly(Recovery recovery) =>
        CrashEverywhere(SetupUnlocked, w => w.Engine.Lock(), recovery);

    [Theory]
    [InlineData(Recovery.UnlockNow)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringUnlockRestoresEverythingExactly(Recovery recovery) =>
        CrashEverywhere(SetupLocked, w => w.Engine.Unlock(), recovery);

    [Theory]
    [InlineData(Recovery.UnlockNow)]
    [InlineData(Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointWhileGuardingRestoresEverythingExactly(Recovery recovery) =>
        CrashEverywhere(
            SetupLocked,
            w =>
            {
                // Busy lock: tampering, a device that leaves and one that arrives.
                w.Mics.OtherAppSetsMute("a", false);
                w.Mics.Unplug("j");
                w.Mics.Plug("g");
                w.Pump();
                w.Engine.Reconcile();
            },
            recovery);

    [Fact]
    public void CrashAtAnyPointDuringLockWithRealFilesRestoresEverythingExactly()
    {
        using var temp = new TempDirectory();
        int n = 1;
        for (; ; n++)
        {
            LockWorld world = SetupUnlockedWithFiles(temp.Path, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            world.Crash.ArmAt(n);
            try
            {
                world.Engine.Lock();
            }
            catch (SimulatedCrashException)
            {
                string context = $"crash at point {n} ({world.Crash.LastCrash})";
                Recover(world, world.Restart(), Recovery.UnlockNow);
                AssertRestored(world, context);
                continue;
            }

            world.Crash.Disarm();
            world.Engine.Unlock();
            AssertRestored(world, "no crash");
            break;
        }

        Assert.True(n > 20, $"Only {n} crash points.");
    }

    /// <summary>
    /// For every crash point N of <paramref name="operation"/>: crash there, recover; and for every
    /// crash point M of that recovery: crash there too, and recover again.
    /// </summary>
    private static void CrashEverywhere(Func<LockWorld> setup, Action<LockWorld> operation, Recovery recovery)
    {
        int runs = 0;
        for (int n = 1; ; n++)
        {
            bool crashedAtN = false;
            for (int m = 0; ; m++)
            {
                LockWorld world = setup();
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
                    AssertRestored(world, "no crash");
                    break; // N is past the last crash point of the operation.
                }

                string context = $"crash at point {n} ({world.Crash.LastCrash})";
                bool crashedAtM = false;
                try
                {
                    Recover(world, world.Restart(crashAt: m), recovery);
                    world.Crash.Disarm();
                }
                catch (SimulatedCrashException)
                {
                    crashedAtM = true;
                    context += $", then at recovery point {m} ({world.Crash.LastCrash})";
                    Recover(world, world.Restart(), recovery);
                }

                AssertRestored(world, context);
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

    private static void Recover(LockWorld world, StartupRecovery? startup, Recovery recovery)
    {
        if (startup is null)
        {
            return; // Crashed before the journal existed, or after it was deleted.
        }

        if (recovery == Recovery.StayLockedThenUnlock)
        {
            if (startup.Phase != LockPhase.Locked)
            {
                world.Engine.ResumeLock();
            }

            Assert.Equal(LockState.Locked, world.Engine.Status.State);
            foreach (string id in new[] { "a", "b", "f", "h", "j" })
            {
                Assert.True(world.Mics[id].Muted || !world.Mics[id].IsReadable, $"{id} must be muted while locked");
            }

            Assert.False(world.Mics["e"].Muted);
        }

        world.Engine.Unlock();
    }

    private static void AssertRestored(LockWorld world, string context)
    {
        Assert.False(world.Engine.HasLock, context);
        Assert.Null(world.Journal.Load());

        foreach (FakeMic mic in world.Mics.All.Where(m => m.IsReadable))
        {
            Assert.True(Original[mic.Id] == mic.Muted, $"{mic} should be {(Original[mic.Id] ? "muted" : "unmuted")} after {context}");
        }

        foreach (string id in NeverTouched)
        {
            Assert.True(world.Mics.SetMuteCallsFor(id) == 0, $"{id} was touched after {context}");
        }

        // Only devices that are away still owe a restore, and they get it when they come back.
        Assert.All(world.Pending.Load(), p => Assert.False(world.Mics[p.TargetId].IsReadable, $"{p.TargetId} pending after {context}"));
        Assert.Contains("i", world.Pending.Load().Select(p => p.TargetId));
        foreach (FakeMic mic in world.Mics.All.Where(m => m.State is EndpointState.NotPresent or EndpointState.Unplugged))
        {
            world.Mics.Plug(mic.Id);
        }

        world.Pump();
        world.Engine.Reconcile();
        foreach (FakeMic mic in world.Mics.All)
        {
            Assert.True(Original[mic.Id] == mic.Muted, $"{mic} should be {(Original[mic.Id] ? "muted" : "unmuted")} after {context} and replug");
        }

        Assert.Empty(world.Pending.Load());
    }

    private static LockWorld SetupUnlocked() => Populate(new LockWorld());

    private static LockWorld SetupLocked()
    {
        LockWorld world = SetupUnlocked();
        world.Engine.Lock();
        world.Pump();
        return world;
    }

    private static LockWorld SetupUnlockedWithFiles(string directory, string name)
    {
        var world = new LockWorld();
        var journal = new FileJournalStore(Path.Combine(directory, name, "lock-journal.json"));
        var pending = new FilePendingRestoreStore(Path.Combine(directory, name, "pending-restores.json"));
        world.UseStores(journal, pending);
        return Populate(world);
    }

    private static LockWorld Populate(LockWorld world)
    {
        FakeMicBackend mics = world.Mics;
        mics.Add("a");
        mics.Add("b", muted: true);
        mics.Add("c", state: EndpointState.Disabled);
        mics.Add("d", state: EndpointState.NotPresent);
        mics.Add("e");
        mics.Add("f", state: EndpointState.Unplugged);
        mics.Add("g", muted: true, state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        mics.Add("h", muted: true, state: EndpointState.NotPresent);
        mics.Add("i", muted: true, state: EndpointState.NotPresent);
        mics.Add("j", hardwareMute: true);
        mics.Defaults = new DefaultMics("a", "a", "j");
        world.Exclude("e", byStableKeyOnly: true);
        world.SeedPendingRestore("h");
        world.SeedPendingRestore("i");
        world.Engine.Initialize();

        // "h" is back but its notification was lost, so it is still muted by the earlier lock.
        mics["h"].State = EndpointState.Active;
        mics.DropNotifications();
        return world;
    }
}
