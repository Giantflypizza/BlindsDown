using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;
using static BlindsDown.Tests.Core.Locking.StrongerLockCrashScenario;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Crashes at every point of a stronger lock.</summary>
public sealed class StrongerLockCrashDuringLockTests
{
    [Theory]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.StayLockedThenUnlock)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringLockRestoresEverythingExactly(Scope scope, Recovery recovery) =>
        CrashEverywhere(() => SetupUnlocked(scope), w => w.Engine.Lock(), recovery, scope);
}

/// <summary>Crashes at every point of the unlock of a stronger lock.</summary>
public sealed class StrongerLockCrashDuringUnlockTests
{
    [Theory]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.StayLockedThenUnlock)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointDuringUnlockRestoresEverythingExactly(Scope scope, Recovery recovery) =>
        CrashEverywhere(() => SetupLocked(scope), w => w.Engine.Unlock(), recovery, scope);
}

/// <summary>Crashes at every point of the guard's work during a busy stronger lock.</summary>
public sealed class StrongerLockCrashWhileGuardingTests
{
    [Theory]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsTurnedOff, Recovery.StayLockedThenUnlock)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.UnlockNow)]
    [InlineData(Scope.DefaultsKeptOn, Recovery.StayLockedThenUnlock)]
    public void CrashAtAnyPointWhileGuardingRestoresEverythingExactly(Scope scope, Recovery recovery) =>
        CrashEverywhere(
            () => SetupLocked(scope),
            w =>
            {
                // Something turns a mic back on and unmutes another, a turned-off mic (a default, when the
                // defaults are off) leaves, and a Bluetooth mic connects.
                w.Mics.Enable("b");
                w.Mics.OtherAppSetsMute("v", false);
                w.Mics.Unplug(scope == Scope.DefaultsTurnedOff ? "j" : "m");
                w.Mics.Plug("g");
                w.Pump();
                w.Engine.Reconcile();
            },
            recovery,
            scope);
}

/// <summary>A disk that fills up in the middle of a stronger lock.</summary>
public sealed class StrongerLockDiskFullTests
{
    [Theory]
    [InlineData(Scope.DefaultsTurnedOff)]
    [InlineData(Scope.DefaultsKeptOn)]
    public void DiskThatFillsUpAfterAnyNumberOfWritesNeverLeavesAnUnrecordedChange(Scope scope)
    {
        for (int n = 1; ; n++)
        {
            LockWorld world = SetupUnlocked(scope);
            var disk = new DiskThatFillsUp(world.Journal, n);
            var engine = new LockEngine(world.Mics, disk, world.Pending, world.Clock, () => world.Settings);
            engine.Lock();
            engine.Reconcile();
            string context = $"disk full after {n} journal writes";

            // Whatever this lock changed, the journal on disk knows about. ("h" and "i" are still muted and off
            // from an earlier lock, and "c" was turned off by the user.)
            LockJournal persisted = world.Journal.Load()!;
            foreach (FakeMic mic in world.Mics.All.Where(m => m.Id is not ("c" or "h" or "i")))
            {
                if (mic.Muted && !OriginalMute[mic.Id])
                {
                    Assert.True(persisted.Actions.Exists(a => a.Kind == ActionKind.MuteMic && a.TargetId == mic.Id && a.Status != ActionStatus.Planned), $"{mic} muted without a record, {context}");
                }

                if (mic.IsOff)
                {
                    Assert.True(persisted.Actions.Exists(a => a.Kind == ActionKind.DisableMicEndpoint && a.TargetId == mic.Id && a.Status != ActionStatus.Planned), $"{mic} turned off without a record, {context}");
                }
            }

            // The app dies; it starts again with room on the disk, and the user restores.
            world.Restart();
            world.Engine.Unlock();
            AssertRestored(world, context);
            if (!disk.Filled)
            {
                Assert.True(n > 15, $"Only {n} journal writes.");
                break; // N is past the last write of the lock.
            }
        }
    }

    /// <summary>A journal store on a disk that is full from the (N+1)-th write of this process on: the write
    /// fails like a full disk. The writes before it reach the inner store, which is what survives.</summary>
    private sealed class DiskThatFillsUp(IJournalStore inner, int writesThatFit) : IJournalStore
    {
        private int _writes;

        public bool Filled => _writes > writesThatFit;

        public LockJournal? Load() => inner.Load();

        public void Save(LockJournal journal)
        {
            if (++_writes > writesThatFit)
            {
                throw new IOException("There is not enough space on the disk.");
            }

            inner.Save(journal);
        }

        public void Delete() => inner.Delete();
    }
}

/// <summary>
/// The stronger lock under crashes: kills the engine at every crash point (before and after every journal
/// write, pending-store write, mute, turn-off, turn-on and default change) of an operation, starts a new engine
/// over what was persisted and recovers the way the UI offers. Every microphone must end with exactly its mute,
/// its on/off state and the defaults it had before the lock. For every third of those points the recovery is
/// killed at every one of its own points too (all of them would take minutes; the mute-only
/// <see cref="LockEngineCrashTests"/> do all).
/// </summary>
public static class StrongerLockCrashScenario
{
    /// <summary>Mute state before the lock.</summary>
    internal static readonly Dictionary<string, bool> OriginalMute = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = false, // console and multimedia default
        ["b"] = true, // already muted: never muted or unmuted, but turned off and back on
        ["c"] = false, // turned off by the user: never touched
        ["e"] = false, // excluded: never touched
        ["g"] = true, // Bluetooth hands-free, unreadable: muted, but nobody knows at lock time
        ["h"] = false, // still muted and off from an earlier lock (pending restore), back
        ["i"] = false, // still muted and off from an earlier lock (pending restore), absent
        ["j"] = false, // communications default, hardware mute
        ["k"] = false, // still muted by an earlier mute-only lock (pending restore), back
        ["m"] = false, // another physical mic
        ["v"] = false, // virtual: only ever muted
    };

    private static readonly DefaultMics OriginalDefaults = new("a", "a", "j");

    private static readonly string[] NeverMuted = ["b", "c", "e", "g"];

    private static readonly string[] NeverSwitched = ["c", "e", "v"];

    /// <summary>Recovery crashes are swept after every this-many-th crash point of the operation.</summary>
    private const int NestedStride = 3;

    public enum Recovery
    {
        /// <summary>"Restore devices" at the startup prompt.</summary>
        UnlockNow,

        /// <summary>"Stay locked", then a normal unlock later.</summary>
        StayLockedThenUnlock,
    }

    /// <summary>Which microphones the lock turns off.</summary>
    public enum Scope
    {
        /// <summary>Every physical one, defaults last: Windows moves the defaults twice ("a" to "j" to "v").</summary>
        DefaultsTurnedOff,

        /// <summary>The excluded "e" is in use, so the defaults are kept on and never move.</summary>
        DefaultsKeptOn,
    }

    /// <summary>
    /// For every crash point N of <paramref name="operation"/>: crash there, recover; and for every third N, for
    /// every crash point M of that recovery: crash there too, and recover again.
    /// </summary>
    internal static void CrashEverywhere(Func<LockWorld> setup, Action<LockWorld> operation, Recovery recovery, Scope scope)
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
                    Recover(world, world.Restart(crashAt: m), recovery, scope);
                    world.Crash.Disarm();
                }
                catch (SimulatedCrashException)
                {
                    crashedAtM = true;
                    context += $", then at recovery point {m} ({world.Crash.LastCrash})";
                    Recover(world, world.Restart(), recovery, scope);
                }

                AssertRestored(world, context);
                runs++;
                if ((m > 0 && !crashedAtM) || n % NestedStride != 1)
                {
                    break; // M is past the last crash point of the recovery (or this N is not swept).
                }
            }

            if (!crashedAtN)
            {
                break;
            }
        }

        Assert.True(runs > 100, $"Only {runs} crash scenarios ran; the fake is not exercising the engine.");
    }

    internal static void AssertRestored(LockWorld world, string context)
    {
        Assert.False(world.Engine.HasLock, context);
        Assert.Null(world.Journal.Load());

        foreach (FakeMic mic in world.Mics.All.Where(m => m.IsReadable))
        {
            Assert.True(OriginalMute[mic.Id] == mic.Muted, $"{mic} should be {(OriginalMute[mic.Id] ? "muted" : "unmuted")} after {context}");
        }

        foreach (string id in NeverMuted)
        {
            Assert.True(world.Mics.SetMuteCallsFor(id) == 0, $"{id} was muted or unmuted after {context}");
        }

        foreach (string id in NeverSwitched)
        {
            Assert.True(world.Mics.SetEndpointEnabledCallsFor(id) == 0, $"{id} was turned off or on after {context}");
        }

        // Only devices that are away, or still off from an earlier lock, still owe a restore; they get it as soon
        // as they are back and BlindsDown notices.
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
            Assert.True(OriginalMute[mic.Id] == mic.Muted, $"{mic} should be {(OriginalMute[mic.Id] ? "muted" : "unmuted")} after {context} and replug");
            Assert.True(mic.IsOff == (mic.Id == "c"), $"{mic} should be {(mic.Id == "c" ? "off" : "on")} after {context} and replug");
        }

        Assert.True(OriginalDefaults == world.Mics.Defaults, $"Defaults are {world.Mics.Defaults} after {context} and replug");
        Assert.Empty(world.Pending.Load());
    }

    internal static LockWorld SetupUnlocked(Scope scope)
    {
        var world = new LockWorld();
        world.Settings.DisableMicsInWindows = true;
        FakeMicBackend mics = world.Mics;
        mics.Add("a");
        mics.Add("b", muted: true);
        mics.Add("c", state: EndpointState.Disabled);
        mics.Add("e", state: scope == Scope.DefaultsKeptOn ? EndpointState.Active : EndpointState.Unplugged);
        mics.Add("g", muted: true, state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        mics.Add("h", muted: true, state: EndpointState.NotPresent).IsOff = true;
        mics.Add("i", muted: true, state: EndpointState.NotPresent).IsOff = true;
        mics.Add("j", hardwareMute: true);
        mics.Add("k", muted: true, state: EndpointState.NotPresent);
        mics.Add("m");
        mics.AddVirtual("v");
        mics.Defaults = OriginalDefaults;
        world.Exclude("e", byStableKeyOnly: true);
        world.SeedPendingTurnOn("h");
        world.SeedPendingTurnOn("i");
        world.SeedPendingRestore("k");
        world.Engine.Initialize();

        // "h" and "k" are back but their notifications were lost, so the earlier locks' changes are still on them.
        mics["h"].Presence = EndpointState.Active;
        mics["k"].Presence = EndpointState.Active;
        mics.DropNotifications();
        return world;
    }

    internal static LockWorld SetupLocked(Scope scope)
    {
        LockWorld world = SetupUnlocked(scope);
        world.Engine.Lock();
        world.Pump();
        return world;
    }

    private static void Recover(LockWorld world, StartupRecovery? startup, Recovery recovery, Scope scope)
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
            foreach (string id in new[] { "a", "b", "j", "k", "m", "v" })
            {
                Assert.True(world.Mics[id].Muted || !world.Mics[id].IsReadable, $"{id} must be muted while locked");
            }

            foreach (string id in scope == Scope.DefaultsTurnedOff ? ["a", "b", "j", "k", "m"] : new[] { "b", "k", "m" })
            {
                Assert.True(world.Mics[id].IsOff, $"{id} must be off while locked");
            }

            Assert.False(world.Mics["e"].Muted || world.Mics["e"].IsOff);
        }

        world.Engine.Unlock();
    }
}
