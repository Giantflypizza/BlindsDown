using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>The guard: anything that unmutes a locked mic is undone and reported, and failures are visible.</summary>
public sealed class LockEngineGuardTests
{
    [Fact]
    public void OtherAppUnmutingWhileLockedIsMutedAgainAndAlerted()
    {
        var world = LockedWorld("a");

        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();

        Assert.True(world.Mics["a"].Muted);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.UnmutedWhileLocked, alert.Kind);
        Assert.True(alert.IsUrgent);
        Assert.Equal(1, world.Action("a").GuardCount);

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void OurOwnChangesAndMuteNotificationsAreIgnored()
    {
        var world = LockedWorld("a");

        world.Engine.OnMuteChanged("a", muted: false, causedByUs: true);
        world.Engine.OnMuteChanged("a", muted: true, causedByUs: false);
        world.Pump();

        Assert.Empty(world.Alerts);
        Assert.Equal(0, world.Action("a").GuardCount);
    }

    [Fact]
    public void UnmuteNotificationsAreIgnoredWhileUnlocked()
    {
        var world = new LockWorld();
        world.Mics.Add("a", muted: true);
        world.Engine.Initialize();

        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();

        Assert.False(world.Mics["a"].Muted);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void RepeatedUnmutesAreThrottledAndBecomeATamperingAlertAfterFiveInAMinute()
    {
        var world = LockedWorld("a");

        for (int i = 1; i <= 4; i++)
        {
            world.Mics.OtherAppSetsMute("a", false);
            world.Pump();
            world.Clock.AdvanceSeconds(2);
            Assert.True(world.Mics["a"].Muted);
        }

        // One alert for four unmutes within the 30 s cooldown.
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));

        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        EngineAlert tamper = Assert.Single(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.True(tamper.IsUrgent);
        Assert.Contains("5 times", tamper.Message, StringComparison.Ordinal);
        Assert.True(world.Mics["a"].Muted);

        // Still fighting: the tampering alert is throttled too.
        world.Clock.AdvanceSeconds(2);
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        Assert.Single(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.True(world.Mics["a"].Muted);

        // After the cooldown, a persistent fight alerts again.
        world.Clock.AdvanceSeconds(31);
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        Assert.Equal(2, world.AlertsOf(EngineAlertKind.Tampering).Count);
        Assert.Equal(7, world.Action("a").GuardCount);
    }

    [Fact]
    public void TamperingWindowForgetsOldUnmutes()
    {
        var world = LockedWorld("a");
        for (int i = 0; i < 4; i++)
        {
            world.Mics.OtherAppSetsMute("a", false);
            world.Pump();
        }

        world.Clock.AdvanceSeconds(61);
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();

        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.Equal(2, world.AlertsOf(EngineAlertKind.UnmutedWhileLocked).Count);
    }

    [Fact]
    public void TamperingIsCountedPerDevice()
    {
        var world = LockedWorld("a", "b");
        for (int i = 0; i < 3; i++)
        {
            world.Mics.OtherAppSetsMute("a", false);
            world.Mics.OtherAppSetsMute("b", false);
            world.Pump();
        }

        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.Equal(2, world.AlertsOf(EngineAlertKind.UnmutedWhileLocked).Count);
    }

    [Fact]
    public void TamperHistoryIsClearedByUnlock()
    {
        var world = LockedWorld("a");
        for (int i = 0; i < 4; i++)
        {
            world.Mics.OtherAppSetsMute("a", false);
            world.Pump();
        }

        world.Engine.Unlock();
        world.Engine.Lock();
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();

        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));
    }

    [Fact]
    public void ReconcileCatchesAMissedUnmuteAndAMissedNewDevice()
    {
        var world = LockedWorld("a");

        world.Mics.OtherAppSetsMute("a", false);
        world.Mics.Add("new");
        world.Mics.DropNotifications();
        world.Engine.Reconcile();

        Assert.True(world.Mics["a"].Muted);
        Assert.True(world.Mics["new"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Single(world.AlertsOf(EngineAlertKind.NewDeviceLocked));

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
        Assert.False(world.Mics["new"].Muted);
    }

    [Fact]
    public void UnmuteNotificationForAnUnknownMicLocksItAsNew()
    {
        var world = LockedWorld("a");
        world.Mics.Add("surprise");
        world.Mics.DropNotifications();

        world.Engine.OnMuteChanged("surprise", muted: false, causedByUs: false);

        Assert.True(world.Mics["surprise"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.NewDeviceLocked));
    }

    [Fact]
    public void ExcludingADeviceDuringTheLockStopsTheFightAndUnlockStillRestoresIt()
    {
        var world = LockedWorld("a", "headset");

        // The user excludes the headset mid-lock and unmutes it to take a call.
        world.Exclude("headset");
        world.Engine.RefreshStatus();
        world.Mics.OtherAppSetsMute("headset", false);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["headset"].Muted);
        Assert.Empty(world.Alerts);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Engine.Unlock();
        Assert.False(world.Mics["headset"].Muted);
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void SetMuteFailureGivesPartialLockAndAnUrgentAlert()
    {
        var world = new LockWorld();
        world.Mics.Add("good");
        world.Mics.Add("stubborn").SetMuteThrows = true;
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.True(status.IsLockedOrPartly);
        string problem = Assert.Single(status.Problems);
        Assert.Contains("Access denied", problem, StringComparison.Ordinal);
        Assert.Equal(DeviceProtection.None, world.DeviceStatus("stubborn").Protection);
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.True(alert.IsUrgent);
        Assert.Equal(ActionStatus.ApplyFailed, world.Action("stubborn").Status);

        // The device starts cooperating: the safety poll finishes the lock.
        world.Mics["stubborn"].SetMuteThrows = false;
        world.Engine.Reconcile();
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.True(world.Mics["stubborn"].Muted);

        world.Engine.Unlock();
        Assert.False(world.Mics["stubborn"].Muted);
        Assert.False(world.Mics["good"].Muted);
    }

    [Fact]
    public void MuteThatDoesNotStickIsReportedAndRetriedWithoutTamperingAlerts()
    {
        var world = new LockWorld();
        world.Mics.Add("stubborn").MuteDoesNotStick = true;
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();
        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.Contains("did not take effect", status.Problems[0], StringComparison.Ordinal);

        for (int i = 0; i < 10; i++)
        {
            world.Clock.AdvanceSeconds(2);
            world.Engine.Reconcile();
        }

        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.Empty(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));

        RestoreReport report = world.Engine.Unlock()!;
        Assert.False(world.Mics["stubborn"].Muted);
        Assert.True(report.AllExact);
    }

    [Fact]
    public void GetMuteFailureCountsAsALockFailure()
    {
        var world = new LockWorld();
        world.Mics.Add("flaky").GetMuteThrows = true;
        world.Engine.Initialize();

        Assert.Equal(LockState.PartiallyLocked, world.Engine.Lock().State);
        Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));

        world.Mics["flaky"].GetMuteThrows = false;
        world.Engine.Reconcile();
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        world.Engine.Unlock();
        Assert.False(world.Mics["flaky"].Muted);
    }

    [Fact]
    public void UnmuteFailureAtUnlockIsReportedAsFailed()
    {
        var world = LockedWorld("a", "b");
        world.Mics["b"].SetMuteThrows = true;

        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Mics["a"].Muted);
        Assert.True(world.Mics["b"].Muted);
        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.Failed);
        Assert.Contains("Access denied", line.Message, StringComparison.Ordinal);
        Assert.Contains("Sound settings", line.Message, StringComparison.Ordinal);
        Assert.Equal("1 restored exactly, 1 needs attention.", report.Summary);
        Assert.False(world.Engine.HasLock);
    }

    [Fact]
    public void UnmuteThatDoesNotStickIsReportedAsFailed()
    {
        var world = LockedWorld("a");
        world.Mics["a"].UnmuteDoesNotStick = true;

        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Contains("Unmute did not take effect", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmutedByOthersDuringUnlockIsReportedAsChangedByOthers()
    {
        var world = new LockWorld();
        world.Mics.Add("a", muted: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        // It was muted before the lock. Something unmutes it while locked and the guard can't mute it
        // again. Unlock never unmutes or mutes a device that was already muted, so it stays unmuted and
        // the report says it was changed by someone else.
        world.Mics["a"].MuteDoesNotStick = true;
        world.Mics.OtherAppSetsMute("a", false);
        world.Pump();
        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.ChangedByOthers, line.Outcome);
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void AlertCooldownIsPerKindAndDevice()
    {
        var world = LockedWorld("a", "b");
        world.Mics["a"].MuteDoesNotStick = true;
        world.Mics["b"].MuteDoesNotStick = true;

        world.Mics.OtherAppSetsMute("a", false);
        world.Mics.OtherAppSetsMute("b", false);
        world.Pump();

        Assert.Equal(2, world.AlertsOf(EngineAlertKind.LockFailed).Count);
        world.Clock.AdvanceSeconds(10);
        world.Engine.Reconcile();
        Assert.Equal(2, world.AlertsOf(EngineAlertKind.LockFailed).Count);
        world.Clock.AdvanceSeconds(25);
        world.Engine.Reconcile();
        Assert.Equal(4, world.AlertsOf(EngineAlertKind.LockFailed).Count);
    }

    private static LockWorld LockedWorld(params string[] ids)
    {
        var world = new LockWorld();
        foreach (string id in ids)
        {
            world.Mics.Add(id);
        }

        world.Engine.Initialize();
        world.Engine.Lock();
        world.Pump();
        world.Alerts.Clear();
        return world;
    }
}
