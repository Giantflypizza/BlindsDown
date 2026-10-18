using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;
using static BlindsDown.Tests.Core.Locking.StrongerLockSupport;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// The stronger lock: physical microphones are muted, then turned off in Windows; unlock turns them back on,
/// then unmutes them. Scope, order, the guard, failures, and devices that come and go.
/// </summary>
public sealed class StrongerLockTests
{
    [Fact]
    public void LockMutesEverythingThenTurnsOffPhysicalMicsAndUnlockReversesIt()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("arctis");
        world.Mics.Add("webcam");
        world.Mics.AddVirtual("sonar");
        world.Engine.Initialize();
        world.Crash.Record();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(
            ["SetMute(arctis, True)", "SetMute(webcam, True)", "SetMute(sonar, True)", "SetEndpointEnabled(arctis, False)", "SetEndpointEnabled(webcam, False)"],
            world.DeviceSteps());
        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(status.Problems);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("arctis").Protection);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("webcam").Protection);
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("sonar").Protection);
        Assert.All(status.Devices, d => Assert.Null(d.Detail));
        Assert.True(world.Mics["arctis"].IsOff && world.Mics["arctis"].Muted);
        Assert.False(world.Mics["sonar"].IsOff);
        Assert.True(world.PersistedJournal!.DisableMicEndpoints);
        Assert.Equal(ActionStatus.Applied, world.TurnOffAction("arctis")!.Status);
        Assert.Null(world.TurnOffAction("sonar"));

        world.Crash.Record();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(
            ["SetEndpointEnabled(webcam, True)", "SetEndpointEnabled(arctis, True)", "SetMute(sonar, False)", "SetMute(webcam, False)", "SetMute(arctis, False)"],
            world.DeviceSteps());
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
        Assert.True(report.AllExact, report.Summary);
        Assert.Equal(3, report.DeviceCount);
        Assert.Equal(3, report.Lines.Count);
        Assert.Equal("Turned back on and unmuted, as before the lock.", report.Lines.Single(l => l.DeviceName == "Microphone (arctis)").Message);
        Assert.Equal("Unmuted, as before the lock.", report.Lines.Single(l => l.DeviceName == "Microphone (sonar)").Message);
        Assert.Equal("All 3 microphones restored exactly as before.", report.Summary);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
    }

    [Fact]
    public void TheStrongerLockIsOffByDefault()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();

        world.Engine.Lock();

        Assert.False(world.Settings.DisableMicsInWindows);
        Assert.False(world.PersistedJournal!.DisableMicEndpoints);
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("a").Protection);
        world.Engine.Unlock();
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
    }

    [Fact]
    public void TheSettingIsReadWhenALockStarts()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();

        // Turned on during a mute-only lock: applies from the next lock.
        world.Settings.DisableMicsInWindows = true;
        world.Mics.Add("late", notify: true);
        world.Pump();
        world.Engine.Reconcile();
        Assert.True(world.Mics["late"].Muted);
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
        world.Engine.Unlock();

        world.Engine.Lock();
        Assert.True(world.Mics["a"].IsOff);
        Assert.True(world.Mics["late"].IsOff);

        // Turned off during a stronger lock: this lock keeps guarding what it turned off.
        world.Settings.DisableMicsInWindows = false;
        world.Mics.Enable("a");
        world.Pump();
        Assert.True(world.Mics["a"].IsOff);

        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void MicsThatWereOffBeforeTheLockAreLeftAlone()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.Add("off", state: EndpointState.Disabled);
        world.Engine.Initialize();

        world.Engine.Lock();

        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("off").Protection);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("a").Protection);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Mics["off"].IsOff);
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("off"));
        Assert.Equal(0, world.Mics.SetMuteCallsFor("off"));
        Assert.Equal(1, report.DeviceCount);
    }

    [Fact]
    public void AlreadyMutedMicIsTurnedOffAndBackOnButStaysMuted()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("muted", muted: true);
        world.Engine.Initialize();

        world.Engine.Lock();
        Assert.True(world.Mics["muted"].IsOff);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Mics["muted"].IsOff);
        Assert.True(world.Mics["muted"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("muted"));
        Assert.Equal("Turned back on and left muted, as before the lock.", Assert.Single(report.Lines).Message);
    }

    [Fact]
    public void UnpluggedJackIsTurnedOffTooAndComesBackUnplugged()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("jack", state: EndpointState.Unplugged);
        world.Engine.Initialize();

        world.Engine.Lock();
        Assert.Equal(EndpointState.Disabled, world.Mics["jack"].State);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("jack").Protection);

        world.Engine.Unlock();
        Assert.Equal(EndpointState.Unplugged, world.Mics["jack"].State);
        Assert.False(world.Mics["jack"].Muted);
    }

    [Fact]
    public void BluetoothMicIsTurnedOffOnceItConnectsAndIsMuted()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("hfp").Protection);

        world.Mics.Plug("hfp");
        world.Pump();

        Assert.True(world.Mics["hfp"].Muted);
        Assert.True(world.Mics["hfp"].IsOff);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("hfp").Protection);
        Assert.Empty(world.Alerts);

        world.Engine.Unlock();
        Assert.True(world.Mics["hfp"].IsOnAndUnmuted());
        Assert.Equal(EndpointState.Active, world.Mics["hfp"].State);
    }

    [Fact]
    public void NewMicWhileLockedIsMutedAndTurnedOffWithOneAlert()
    {
        LockWorld world = LockedStronger("a");

        world.Mics.Add("webcam-mic", notify: true);
        world.Pump();

        Assert.True(world.Mics["webcam-mic"].Muted);
        Assert.True(world.Mics["webcam-mic"].IsOff);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.NewDeviceLocked, alert.Kind);
        Assert.Contains("muted it and turned it off", alert.Message, StringComparison.Ordinal);
        Assert.True(world.TurnOffAction("webcam-mic")!.AddedWhileLocked);

        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(world.Mics["webcam-mic"].IsOnAndUnmuted());
        Assert.Equal(2, report.DeviceCount);
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void NewVirtualMicWhileLockedIsOnlyMuted()
    {
        LockWorld world = LockedStronger("a");

        world.Mics.AddVirtual("voicemeeter");
        world.Mics.NotifyEndpoint("voicemeeter");
        world.Pump();

        Assert.True(world.Mics["voicemeeter"].Muted);
        Assert.False(world.Mics["voicemeeter"].IsOff);
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("voicemeeter"));
        Assert.Contains("BlindsDown muted it.", Assert.Single(world.Alerts).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MicTurnedBackOnWhileLockedIsTurnedOffAgainWithAnUrgentAlert()
    {
        LockWorld world = LockedStronger("a");

        world.Mics.Enable("a");
        world.Pump();

        Assert.True(world.Mics["a"].IsOff);
        Assert.True(world.Mics["a"].Muted);
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.ReEnabledWhileLocked, alert.Kind);
        Assert.True(alert.IsUrgent);
        Assert.Equal("Something turned \"Microphone (a)\" back on while locked. BlindsDown turned it off again.", alert.Message);
        Assert.Equal(1, world.TurnOffAction("a")!.GuardCount);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Engine.Unlock();
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
    }

    [Fact]
    public void RepeatedTurnOnsAreThrottledAndBecomeATamperingAlert()
    {
        LockWorld world = LockedStronger("a");

        for (int i = 1; i <= 4; i++)
        {
            world.Mics.Enable("a");
            world.Pump();
            world.Clock.AdvanceSeconds(2);
            Assert.True(world.Mics["a"].IsOff);
        }

        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.Empty(world.AlertsOf(EngineAlertKind.Tampering));

        world.Mics.Enable("a");
        world.Pump();
        EngineAlert tamper = Assert.Single(world.AlertsOf(EngineAlertKind.Tampering));
        Assert.True(tamper.IsUrgent);
        Assert.Contains("keeps turning \"Microphone (a)\" back on (5 times", tamper.Message, StringComparison.Ordinal);
        Assert.True(world.Mics["a"].IsOff);
        Assert.Equal(5, world.TurnOffAction("a")!.GuardCount);
    }

    [Fact]
    public void SafetyPollCatchesAMissedTurnOn()
    {
        LockWorld world = LockedStronger("a", "b");

        world.Mics.Enable("b");
        world.Mics.DropNotifications();
        world.Engine.Reconcile();

        Assert.True(world.Mics["b"].IsOff);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void ExcludingATurnedOffMicDuringTheLockStopsTheGuardAndUnlockStillRestoresIt()
    {
        LockWorld world = LockedStronger("a", "headset");

        // The user excludes the headset mid-lock, turns it on and unmutes it to take a call.
        world.Exclude("headset");
        world.Engine.RefreshStatus();
        world.Mics.Enable("headset");
        world.Pump();
        world.Mics.OtherAppSetsMute("headset", false);
        world.Pump();
        world.Engine.Reconcile();

        Assert.True(world.Mics["headset"].IsOnAndUnmuted());
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("headset").Protection);
        Assert.Empty(world.Alerts);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        RestoreReport report = world.Engine.Unlock()!;
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void MicUnpluggedWhileOffComesBackOffAndStaysProtectedQuietly()
    {
        LockWorld world = LockedStronger("usb");

        world.Mics.Unplug("usb");
        world.Pump();
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("usb").Protection);
        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Reconcile();

        Assert.Equal(EndpointState.Disabled, world.Mics["usb"].State);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("usb").Protection);
        Assert.Empty(world.Alerts);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        world.Engine.Unlock();
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
    }

    [Fact]
    public void MicThatComesBackOnAfterAReplugIsTurnedOffAgain()
    {
        LockWorld world = LockedStronger("usb");

        world.Mics.Unplug("usb");
        world.Pump();
        world.Mics["usb"].IsOff = false; // This device forgets the setting when it is unplugged.
        world.Mics.Plug("usb");
        world.Pump();

        Assert.True(world.Mics["usb"].IsOff);
        Assert.True(world.Mics["usb"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        world.Engine.Unlock();
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
    }

    [Fact]
    public void MicUnpluggedAtUnlockIsTurnedOnAndUnmutedWhenItComesBack()
    {
        LockWorld world = LockedStronger("stays", "usb");
        world.Mics.Unplug("usb");
        world.Pump();

        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = report.Lines.Single(l => l.DeviceName == "Microphone (usb)");
        Assert.Equal(RestoreOutcome.Pending, line.Outcome);
        Assert.Equal("Not connected right now. It will be turned back on and unmuted when it is plugged back in.", line.Message);
        Assert.Equal(1, report.PendingCount);
        Assert.Equal(2, report.DeviceCount);
        Assert.Equal(2, world.Pending.Load().Count);
        Assert.True(world.Mics["stays"].IsOnAndUnmuted());

        // Back while unlocked: still off (Windows kept the setting), so it is turned on, then unmuted.
        world.Crash.Record();
        world.Mics.Plug("usb");
        world.Pump();

        Assert.Equal(["SetEndpointEnabled(usb, True)", "SetMute(usb, False)"], world.DeviceSteps());
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
        EngineAlert done = Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
        Assert.Equal("\"Microphone (usb)\" is back and was restored (turned back on and unmuted, as before the lock).", done.Message);
    }

    [Fact]
    public void MicUnpluggedAtUnlockIsRestoredAfterARestartToo()
    {
        LockWorld world = LockedStronger("usb");
        world.Mics.Unplug("usb");
        world.Engine.Unlock();
        world.Mics.Plug("usb");
        world.Mics.DropNotifications();

        Assert.Null(world.Restart());

        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void PendingTurnOnIsDroppedWhenSomeoneElseTurnedTheMicOnAlready()
    {
        LockWorld world = LockedStronger("usb");
        world.Mics.Unplug("usb");
        world.Engine.Unlock();
        world.Mics["usb"].IsOff = false;

        world.Mics.Plug("usb");
        world.Pump();

        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
        Assert.Equal(1, world.Mics.SetEndpointEnabledCallsFor("usb")); // Only the turn-off of the lock.
        Assert.Contains("(unmuted, as before the lock)", Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MicOwedATurnOnThatIsUsableAtTheNextLockDropsTheOldEntry()
    {
        LockWorld world = LockedStronger("usb");
        world.Mics.Unplug("usb");
        world.Engine.Unlock();

        // It comes back on (notification lost) and the user locks before the safety poll.
        world.Mics["usb"].IsOff = false;
        world.Mics.Plug("usb");
        world.Mics.DropNotifications();
        world.Engine.Lock();

        Assert.Empty(world.Pending.Load());
        Assert.False(world.MuteAction("usb").WasAlreadyLocked);
        Assert.True(world.Mics["usb"].IsOff);
        world.Engine.Unlock();
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
    }

    [Theory]
    [InlineData(DeviceErrorKind.AccessDenied, "administrator rights")]
    [InlineData(DeviceErrorKind.NotSupported, "doesn't support turning microphones off")]
    public void WindowsRefusingTheTurnOffGivesOneAlertAndAMuteOnlyLock(DeviceErrorKind refusal, string reason)
    {
        LockWorld world = StrongerWorld();
        world.Mics.EndpointDisableDenied = refusal == DeviceErrorKind.AccessDenied;
        world.Mics.EndpointDisableNotSupported = refusal == DeviceErrorKind.NotSupported;
        world.Mics.Add("a");
        world.Mics.Add("b");
        world.Mics.Add("c");
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(status.Problems);
        Assert.All(world.Mics.All, m => Assert.True(m.Muted && !m.IsOff, m.ToString()));
        Assert.All(status.Devices, d => Assert.Equal(DeviceProtection.SoftwareMute, d.Protection));
        EngineAlert alert = Assert.Single(world.Alerts);
        Assert.Equal(EngineAlertKind.StrongerLockUnavailable, alert.Kind);
        Assert.False(alert.IsUrgent);
        Assert.Contains(reason, alert.Message, StringComparison.Ordinal);
        Assert.Contains("They are still locked.", alert.Message, StringComparison.Ordinal);
        Assert.Single(world.Mics.SetEndpointEnabledLog);
        Assert.True(world.PersistedJournal!.EndpointDisableUnavailable);
        Assert.DoesNotContain(world.PersistedJournal.Actions, a => a.Kind == ActionKind.DisableMicEndpoint);

        // The rest of this lock only mutes, even after a restart: no more attempts, no more alerts.
        world.Mics.Add("late", notify: true);
        world.Pump();
        world.Engine.Reconcile();
        world.Restart();
        world.Engine.Reconcile();
        Assert.True(world.Mics["late"].Muted);
        Assert.Single(world.Mics.SetEndpointEnabledLog);
        Assert.Single(world.AlertsOf(EngineAlertKind.StrongerLockUnavailable));

        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(report.AllExact, report.Summary);
        Assert.All(report.Lines, l => Assert.Equal("Unmuted, as before the lock.", l.Message));
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));

        // The next lock tries again.
        world.Mics.EndpointDisableDenied = false;
        world.Mics.EndpointDisableNotSupported = false;
        world.Engine.Lock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOff, m.ToString()));
    }

    [Fact]
    public void TurnOffThatDoesNotStickIsRetriedQuietlyWhileTheMuteProtects()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("good");
        world.Mics.Add("stubborn").TurnOffDoesNotStick = true;
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(status.Problems);
        DeviceLockInfo stubborn = world.DeviceStatus("stubborn");
        Assert.Equal(DeviceProtection.SoftwareMute, stubborn.Protection);
        Assert.Null(stubborn.Problem);
        Assert.Equal("Not turned off in Windows: Windows did not turn it off.", stubborn.Detail);
        Assert.Equal(ActionStatus.ApplyFailed, world.TurnOffAction("stubborn")!.Status);
        int saves = world.MemoryJournal.SaveCount;

        // Windows can take seconds to refuse, so the safety poll (every few seconds) asks again only every 30 s,
        // and its notifications don't ask sooner.
        for (int i = 0; i < 20; i++)
        {
            world.Clock.AdvanceSeconds(3);
            world.Engine.Reconcile();
            world.Engine.OnEndpointChanged("stubborn");
        }

        Assert.Equal(saves, world.MemoryJournal.SaveCount);
        Assert.Equal(3, world.Mics.SetEndpointEnabledCallsFor("stubborn")); // At 0, 30 and 60 seconds.
        Assert.Empty(world.Alerts);

        world.Mics["stubborn"].TurnOffDoesNotStick = false;
        world.Engine.Reconcile();
        Assert.False(world.Mics["stubborn"].IsOff);
        world.Clock.AdvanceSeconds(30);
        world.Engine.Reconcile();
        Assert.True(world.Mics["stubborn"].IsOff);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("stubborn").Protection);
        Assert.Empty(world.Alerts);

        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void OtherTurnOffFailuresAreANoteNotAProblem()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("busy").SetEnabledThrows = true;
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.Equal("Not turned off in Windows: The device is busy (simulated).", world.DeviceStatus("busy").Detail);
        Assert.Empty(world.Alerts);

        // Still counted as possibly changed, so a crash now can't forget it.
        Assert.Equal(1, world.Restart()!.DevicesChanged);
        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(world.Mics["busy"].IsOnAndUnmuted());
        Assert.Equal("Unmuted, as before the lock.", Assert.Single(report.Lines).Message);
    }

    [Fact]
    public void TurnOnFailureAtUnlockIsReportedAndTheMicIsUnmutedOnceItIsOn()
    {
        LockWorld world = LockedStronger("a", "b");
        world.Mics["b"].SetEnabledThrows = true;

        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Mics["a"].IsOnAndUnmuted());
        Assert.True(world.Mics["b"].IsOff);
        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.Failed);
        Assert.Equal("Could not be turned back on: The device is busy (simulated). Turn it on in Windows Sound settings.", line.Message);
        Assert.Equal("1 restored exactly, 1 needs attention.", report.Summary);
        Assert.False(world.Engine.HasLock);

        // The user turns it on in Sound settings: BlindsDown still owes it the unmute.
        world.Mics["b"].SetEnabledThrows = false;
        world.Mics.Enable("b");
        world.Pump();

        Assert.True(world.Mics["b"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
        Assert.Contains("(unmuted, as before the lock)", Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnOnThatDoesNotStickIsReportedAsFailed()
    {
        LockWorld world = LockedStronger("a");
        world.Mics["a"].TurnOnDoesNotStick = true;

        RestoreReport report = world.Engine.Unlock()!;

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Contains("Windows did not turn it back on", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedDeferredTurnOnIsReportedOnceAndDropped()
    {
        LockWorld world = LockedStronger("usb");
        world.Mics.Unplug("usb");
        world.Engine.Unlock();
        world.Mics["usb"].SetEnabledThrows = true;

        world.Mics.Plug("usb");
        world.Pump();

        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.True(alert.IsUrgent);
        Assert.Equal("Could not restore \"Microphone (usb)\": The device is busy (simulated). Turn it on in Windows Sound settings.", alert.Message);

        // The unmute is still owed and happens once the user turns it on.
        LockAction owed = Assert.Single(world.Pending.Load());
        Assert.Equal(ActionKind.MuteMic, owed.Kind);
        world.Mics["usb"].SetEnabledThrows = false;
        world.Mics.Enable("usb");
        world.Pump();
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
    }

    [Fact]
    public void TurnOffIsWrittenToTheJournalBeforeItHappens()
    {
        static LockWorld Setup()
        {
            LockWorld world = StrongerWorld();
            world.Mics.Add("a");
            world.Engine.Initialize();
            return world;
        }

        int point = CrashPlan.Find(Setup, w => w.Engine.Lock(), "before SetEndpointEnabled(a, False)");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());

        Assert.Equal(ActionStatus.Applying, world.TurnOffAction("a")!.Status);
        Assert.Equal(ActionStatus.Applied, world.MuteAction("a").Status);
    }

    [Fact]
    public void TurnOffThatCannotBeRecordedIsNotMadeUntilTheDiskHasRoomAgain()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        int saves = 0;
        bool diskFills = true;

        // Saves 1-3 are the new journal and the mute's two records; the turn-off's record is the fourth.
        var disk = new FailAfterSaves(new MemoryJournalStore(), () => diskFills && ++saves > 3);
        var engine = new LockEngine(world.Mics, disk, world.Pending, world.Clock, () => world.Settings);
        var alerts = new List<EngineAlert>();
        engine.AlertRaised += alerts.Add;
        engine.Initialize();

        LockStatusInfo status = engine.Lock();

        Assert.True(world.Mics["a"].Muted);
        Assert.False(world.Mics["a"].IsOff);
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
        Assert.Contains(alerts, a => a.Kind == EngineAlertKind.LockFailed && a.DeviceName == "lock record");
        Assert.Equal(LockState.Locked, status.State); // The mute protects it.

        diskFills = false;
        engine.Reconcile();
        Assert.True(world.Mics["a"].IsOff);
        engine.Unlock();
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
    }

    [Fact]
    public void RestartWhileLockedKeepsMicsOffAndCatchesUp()
    {
        LockWorld world = LockedStronger("a", "b");

        // Killed; meanwhile something turned "b" on and a new mic was plugged in.
        world.Mics.Enable("b");
        world.Mics.Add("new");
        StartupRecovery recovery = world.Restart()!;

        Assert.False(recovery.ExitedCleanly);
        Assert.Equal(3, recovery.DevicesChanged); // Counted after the restart caught up, so "new" too.
        Assert.True(world.Mics["b"].IsOff);
        Assert.True(world.Mics["new"].IsOff && world.Mics["new"].Muted);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void DevicesChangedCountsDevicesNotChanges()
    {
        LockWorld world = LockedStronger("a", "b");
        world.Mics.Add("muted", muted: true, notify: true);
        world.Pump();

        // "a" and "b" were muted and turned off (four changes); "muted" was only turned off.
        Assert.Equal(3, world.Restart()!.DevicesChanged);
    }

    /// <summary>Saves through to <paramref name="inner"/> until <paramref name="fail"/> says the disk is full.</summary>
    private sealed class FailAfterSaves(IJournalStore inner, Func<bool> fail) : IJournalStore
    {
        public LockJournal? Load() => inner.Load();

        public void Save(LockJournal journal)
        {
            if (fail())
            {
                throw new IOException("There is not enough space on the disk.");
            }

            inner.Save(journal);
        }

        public void Delete() => inner.Delete();
    }
}
