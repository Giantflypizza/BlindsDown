using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Edge cases found in review: failures at the worst moment, fights with other apps, and lying status.</summary>
public sealed class LockEngineReviewTests
{
    [Fact]
    public void MicWhoseFirstReadFailsKeepsItsTrueOriginalState()
    {
        // A Bluetooth hands-free mic the user had muted can't be read at lock time. When it connects,
        // the very first read fails; the next one works. BlindsDown never muted it, so unlock must not unmute it.
        var world = new LockWorld();
        world.Mics.Add("hfp", muted: true, state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics["hfp"].GetMuteThrows = true;
        world.Mics.Plug("hfp");
        world.Pump();
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        LockAction untouched = world.Action("hfp");
        Assert.Equal(ActionStatus.Planned, untouched.Status);
        Assert.Contains("GetMute failed", untouched.Note, StringComparison.Ordinal);
        Assert.Contains(world.Engine.Status.Problems, p => p.Contains("GetMute failed", StringComparison.Ordinal));

        world.Mics["hfp"].GetMuteThrows = false;
        world.Engine.Reconcile();
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Mics["hfp"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("hfp"));
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void FirstReadFailureBeforeAnyChangeIsNotUndoneAtUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("flaky").GetMuteThrows = true;
        world.Engine.Initialize();
        world.Engine.Lock();

        // It never worked during the lock, so BlindsDown never changed it; a crash now owes it nothing.
        Assert.Equal(0, world.Restart()!.DevicesChanged);
        world.Mics["flaky"].GetMuteThrows = false;
        world.Engine.Unlock();

        Assert.False(world.Mics["flaky"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("flaky"));
    }

    [Fact]
    public void MuteFailureAfterTheWriteAheadCountsAsPossiblyMuted()
    {
        // The mute was attempted, so the mic may be muted even though it could not be verified: the
        // startup prompt must count it, and unlock must check it.
        var world = new LockWorld();
        world.Mics.Add("a").MuteDoesNotStick = true;
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Equal(ActionStatus.ApplyFailed, world.Action("a").Status);

        Assert.Equal(1, world.Restart()!.DevicesChanged);
        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void FightWithAnotherAppKeepsTheMicMutedWithoutAStreamOfJournalWrites()
    {
        // An app that unmutes the mic every time BlindsDown mutes it must not turn every round into
        // flushed disk writes (two per round before). Only the two alerts save the guard count.
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();
        int saves = world.MemoryJournal.SaveCount;

        for (int i = 0; i < 50; i++)
        {
            world.Clock.AdvanceSeconds(0.1);
            world.Mics.OtherAppSetsMute("a", false);
            world.Pump();
            Assert.True(world.Mics["a"].Muted);
        }

        Assert.Equal(saves + 2, world.MemoryJournal.SaveCount);
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Single(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.Equal(5, world.Action("a").GuardCount);

        // The rest of the count rides along with the next save.
        world.Engine.MarkCleanExit();
        Assert.Equal(50, world.Action("a").GuardCount);
        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void PersistentlyFailingMicDoesNotRewriteTheJournalOnEverySafetyPoll()
    {
        var world = new LockWorld();
        world.Mics.Add("stubborn").SetMuteThrows = true;
        world.Engine.Initialize();
        world.Engine.Lock();
        int saves = world.MemoryJournal.SaveCount;

        for (int i = 0; i < 10; i++)
        {
            world.Clock.AdvanceSeconds(3);
            world.Engine.Reconcile();
        }

        Assert.Equal(saves, world.MemoryJournal.SaveCount);
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Status.State);
    }

    [Fact]
    public void LockThatCannotListTheMicrophonesIsNotReportedAsLocked()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Mics.EnumerateThrows = true;

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.Contains(status.Problems, p => p.Contains("list", StringComparison.Ordinal));
        Assert.False(world.Mics["a"].Muted);

        world.Mics.EnumerateThrows = false;
        world.Engine.Reconcile();
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Empty(world.Engine.Status.Problems);
    }

    [Fact]
    public void UnlockedStatusDoesNotComplainWhenTheMicrophonesCannotBeListed()
    {
        var world = new LockWorld();
        world.Mics.EnumerateThrows = true;

        world.Engine.Initialize();

        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
        Assert.Empty(world.Engine.Status.Problems);
    }

    [Fact]
    public void RestoreOwedByAnEarlierLockSurvivesAPendingStoreReadFailureWhileTheMicIsPresent()
    {
        // An earlier lock muted "usb" and could not unmute it (it was away). It is back, still muted,
        // and the next lock can't read the pending-restore file at that moment, so that lock sees a muted
        // mic. That lock must not decide the earlier restore is obsolete.
        var world = new LockWorld();
        world.Mics.Add("usb", muted: true);
        world.SeedPendingRestore("usb");
        var flaky = new FlakyPendingRestoreStore(world.Pending);
        var engine = new LockEngine(world.Mics, world.Journal, flaky, world.Clock, () => world.Settings);

        flaky.FailLoads = true;
        engine.Lock();
        flaky.FailLoads = false;
        engine.Unlock();
        engine.Reconcile();

        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void JournalActionsWithoutATargetAreDropped()
    {
        using var temp = new TempDirectory();
        string path = temp.File("lock-journal.json");
        File.WriteAllText(path, "{ \"phase\": \"Locked\", \"actions\": [ { \"targetId\": null }, { \"targetId\": \"\" }, { \"targetId\": \"x\" } ] }");
        File.WriteAllText(temp.File("pending.json"), "[ { \"targetId\": null }, { \"targetId\": \"y\" } ]");

        Assert.Equal("x", Assert.Single(new FileJournalStore(path).Load()!.Actions).TargetId);
        Assert.Equal("y", Assert.Single(new FilePendingRestoreStore(temp.File("pending.json")).Load()).TargetId);
    }
}
