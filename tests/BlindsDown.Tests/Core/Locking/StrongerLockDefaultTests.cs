using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;
using static BlindsDown.Tests.Core.Locking.StrongerLockSupport;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// Default microphones: turning a default microphone off makes Windows pick another one, so unlock must put
/// every default back (and never one the user picked), and a default microphone must never be turned off while
/// Windows could move apps onto a microphone the lock leaves open. The fake moves defaults like Windows and,
/// pessimistically, never switches back by itself.
/// </summary>
public sealed class StrongerLockDefaultTests
{
    private static readonly DefaultMics Original = new("a", "a", "j");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultMicsAreTurnedOffLastAndUnlockPutsEveryDefaultBack(bool windowsSwitchesBack)
    {
        LockWorld world = DefaultsWorld();
        world.Mics.ReturningDefaultTakesOver = windowsSwitchesBack;
        world.Crash.Record();

        world.Engine.Lock();

        // Windows moved the defaults twice: "a" off -> "j"; "j" off -> the virtual "v".
        Assert.Equal(
            [
                "SetMute(m, True)", "SetMute(v, True)", "SetMute(a, True)", "SetMute(j, True)",
                "SetEndpointEnabled(m, False)", "SetEndpointEnabled(a, False)", "SetEndpointEnabled(j, False)",
            ],
            world.DeviceSteps());
        Assert.Equal(new DefaultMics("v", "v", "v"), world.Mics.Defaults);
        Assert.Equal(new DefaultMics("v", "v", "v"), world.PersistedJournal!.DefaultsWhileLocked);

        world.Crash.Record();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(Original, world.Mics.Defaults);
        if (windowsSwitchesBack)
        {
            Assert.Empty(world.Mics.SetDefaultLog); // Windows did it as the microphones came back on.
        }
        else
        {
            Assert.Equal([("a", MicRole.Console), ("a", MicRole.Multimedia), ("j", MicRole.Communications)], world.Mics.SetDefaultLog);
            Assert.Equal(["SetDefaultMic(a, Console)", "SetDefaultMic(a, Multimedia)", "SetDefaultMic(j, Communications)"], world.DeviceSteps().TakeLast(3));
        }

        Assert.True(report.AllExact, report.Summary);
        Assert.DoesNotContain(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void DefaultTheUserPicksDuringTheLockIsKept()
    {
        LockWorld world = DefaultsWorld();
        world.Mics.AddVirtual("v2");
        world.Engine.Lock();

        // The user picks another communications microphone in Sound settings during the lock.
        world.Mics.UserSetsDefault("v2", MicRole.Communications);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new DefaultMics("a", "a", "v2"), world.Mics.Defaults);
        Assert.DoesNotContain(world.Mics.SetDefaultLog, s => s.Role == MicRole.Communications);
        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
        Assert.Equal("The default communications microphone changed from \"Microphone (j)\" to \"Microphone (v2)\".", line.Message);
    }

    [Fact]
    public void MuteOnlyLockNeverWritesDefaults()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Mics.Add("b");
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Null(world.PersistedJournal!.DefaultsWhileLocked);

        // Defaults that change during the lock (the user, or a device leaving) are not BlindsDown's doing.
        world.Mics.Defaults = new DefaultMics("b", "b", "b");
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Empty(world.Mics.SetDefaultLog);
        Assert.Equal(new DefaultMics("b", "b", "b"), world.Mics.Defaults);
        Assert.Equal(2, report.Lines.Count(l => l.Outcome == RestoreOutcome.DefaultChanged));
    }

    [Fact]
    public void FailedDefaultRestoreIsShownInTheReport()
    {
        LockWorld world = DefaultsWorld();
        world.Engine.Lock();
        world.Mics.SetDefaultThrows = true;

        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Engine.HasLock);
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
        Assert.Equal(2, report.Lines.Count(l => l.Outcome == RestoreOutcome.DefaultChanged));
        Assert.Equal("4 restored exactly, 2 need attention.", report.Summary);
    }

    [Fact]
    public void DefaultMicUnpluggedAtUnlockGetsItsDefaultBackWhenItReturns()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Equal(new DefaultMics("sonar", "sonar", "sonar"), world.Mics.Defaults);

        world.Mics.Unplug("usb");
        world.Pump();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(1, report.PendingCount);
        Assert.Equal(2, report.Lines.Count(l => l.Outcome == RestoreOutcome.DefaultChanged));
        Assert.Empty(world.Mics.SetDefaultLog);
        Assert.Contains(world.Pending.Load(), p => p.DefaultsToRestore == new DefaultMics("sonar", "sonar", "sonar"));

        world.Mics.Plug("usb");
        world.Pump();

        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.Equal(new DefaultMics("usb", "usb", "usb"), world.Mics.Defaults);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void ReturningDefaultMicGetsItsDefaultBackEvenWhenItCannotBeUnmuted()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Unlock();

        world.Mics["usb"].UnmuteDoesNotStick = true;
        world.Mics.Plug("usb");
        world.Pump();

        Assert.False(world.Mics["usb"].IsOff);
        Assert.Contains("Unmute it in Windows Sound settings", Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed)).Message, StringComparison.Ordinal);
        Assert.Equal(new DefaultMics("usb", "usb", "usb"), world.Mics.Defaults);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void DefaultOwedToAReturningMicIsNotForcedWhenSomeoneChangedItSince()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.AddVirtual("voicemeeter");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Engine.Unlock();

        // After the unlock the user makes Voicemeeter the communications default.
        world.Mics.UserSetsDefault("voicemeeter", MicRole.Communications);
        world.Mics.Plug("usb");
        world.Pump();

        Assert.Equal(new DefaultMics("usb", "usb", "voicemeeter"), world.Mics.Defaults);
    }

    [Fact]
    public void BluetoothDefaultThatDisconnectedDuringTheLockGetsItsDefaultBackWhenItReconnects()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("airpods", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97", unreadableWhenUnplugged: true);
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("airpods", "airpods", "airpods");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("airpods", EndpointState.Unplugged);
        world.Pump();

        RestoreReport report = world.Engine.Unlock()!;

        // Turned back on right away (it is still known to Windows), unmuted and made the default when it reconnects.
        Assert.False(world.Mics["airpods"].IsOff);
        Assert.Equal(
            "Turned back on, but not connected right now. It will be unmuted when it is plugged back in.",
            report.Lines.Single(l => l.DeviceName == "Microphone (airpods)").Message);

        world.Mics.Plug("airpods");
        world.Pump();

        Assert.True(world.Mics["airpods"].IsOnAndUnmuted());
        Assert.Equal(new DefaultMics("airpods", "airpods", "airpods"), world.Mics.Defaults);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void ExcludedHeadsetInUseKeepsTheDefaultMicOn()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("webcam");
        world.Mics.Add("headset", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Exclude("headset");
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        // Turning "desk" off would make Windows move apps that follow the default to the excluded headset.
        Assert.Equal(LockState.Locked, status.State);
        Assert.Empty(status.Problems);
        Assert.True(world.Mics["desk"].Muted);
        Assert.False(world.Mics["desk"].IsOff);
        DeviceLockInfo desk = world.DeviceStatus("desk");
        Assert.Equal(DeviceProtection.SoftwareMute, desk.Protection);
        Assert.Null(desk.Problem);
        Assert.Equal("Kept on so Windows doesn't switch apps to \"Microphone (headset)\".", desk.Detail);
        Assert.True(world.Mics["webcam"].IsOff);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("headset").Protection);
        Assert.Equal(new DefaultMics("desk", "desk", "desk"), world.Mics.Defaults);
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("desk"));
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("headset"));
        Assert.Equal(0, world.Mics.SetMuteCallsFor("headset"));

        // The decision holds for the whole lock, even when the headset leaves: the default never moves halfway.
        world.Mics.Unplug("headset", EndpointState.Unplugged);
        world.Pump();
        world.Engine.Reconcile();
        Assert.False(world.Mics["desk"].IsOff);
        Assert.Equal(desk.Detail, world.DeviceStatus("desk").Detail);

        // The helper must leave both the excluded headset's and the default microphone's hardware on.
        Assert.Equal(
            new[] { "desk", "headset" }.Select(id => world.Mics[id].HardwareInstanceId!).Order(StringComparer.Ordinal),
            world.Engine.HardwareToKeepEnabled().Order(StringComparer.Ordinal));

        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(report.AllExact, report.Summary);
        Assert.Empty(world.Mics.SetDefaultLog);
        Assert.Equal("Unmuted, as before the lock.", report.Lines.Single(l => l.DeviceName == "Microphone (desk)").Message);
        Assert.Equal(3, report.DeviceCount);
    }

    [Fact]
    public void KeptOnDefaultTheUserTurnsOffDuringTheLockStaysOffAndIsUnmutedOnceTurnedOn()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("headset", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Exclude("headset");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Equal(ActionStatus.Planned, world.TurnOffAction("desk")!.Status);

        // The user turns the kept-on default off in Sound settings: that is their choice, not BlindsDown's.
        world.Mics.Disable("desk");
        world.Pump();
        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("desk").Protection);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Mics["desk"].IsOff);
        Assert.Empty(world.Mics.SetEndpointEnabledLog);
        RestoreLine line = report.Lines.Single(l => l.DeviceName == "Microphone (desk)");
        Assert.Equal(RestoreOutcome.Pending, line.Outcome);
        Assert.Equal("Turned off in Windows right now (not by BlindsDown). It will be unmuted when it is turned back on.", line.Message);

        world.Mics.Enable("desk");
        world.Pump();
        Assert.True(world.Mics["desk"].IsOnAndUnmuted());
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void ExcludedMicThatIsNotConnectedDoesNotKeepTheDefaultOn()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("headset", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Exclude("headset");
        world.Engine.Initialize();

        world.Engine.Lock();

        Assert.True(world.Mics["desk"].IsOff);
        Assert.Equal(new DefaultMics("sonar", "sonar", "sonar"), world.Mics.Defaults);
        Assert.Equal([world.Mics["headset"].HardwareInstanceId!], world.Engine.HardwareToKeepEnabled());
        world.Engine.Unlock();
        Assert.Equal(new DefaultMics("desk", "desk", "desk"), world.Mics.Defaults);
    }

    [Fact]
    public void MicWhoseMuteFailedKeepsTheDefaultOn()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("stubborn").SetMuteThrows = true;
        world.Mics.Add("webcam");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.False(world.Mics["desk"].IsOff);
        Assert.Equal("Kept on so Windows doesn't switch apps to \"Microphone (stubborn)\".", world.DeviceStatus("desk").Detail);
        Assert.True(world.Mics["webcam"].IsOff);
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("stubborn")); // Never turned off before it is muted.

        // It starts cooperating: muted, then turned off. The default stays where it is for this lock.
        world.Mics["stubborn"].SetMuteThrows = false;
        world.Engine.Reconcile();
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.True(world.Mics["stubborn"].IsOff);
        Assert.False(world.Mics["desk"].IsOff);
        Assert.Equal(new DefaultMics("desk", "desk", "desk"), world.Mics.Defaults);

        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void DefaultTurnedBackOnWhileAMicIsOpenStaysOn()
    {
        LockWorld world = DefaultsWorld();
        world.Engine.Lock();

        // The user plugs in a headset and excludes it, then something turns the old default back on and Windows
        // makes it the default again: turning it off now would move apps onto the headset.
        world.Mics.Add("headset", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97");
        world.Exclude("headset");
        world.Mics.ReturningDefaultTakesOver = true;
        world.Mics.Enable("a");
        Assert.Equal(new DefaultMics("a", "a", "v"), world.Mics.Defaults);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["a"].IsOff);
        Assert.True(world.Mics["a"].Muted);
        Assert.Equal(ActionStatus.Planned, world.TurnOffAction("a")!.Status);
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));

        world.Engine.Unlock();
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
        Assert.Equal(Original, world.Mics.Defaults);
    }

    [Fact]
    public void DefaultTheUserTurnsBackOnAndPicksAgainIsPutBackAfterTheGuardTurnsItOff()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.AddVirtual("v");
        world.Mics.AddVirtual("v2");
        world.Mics.DefaultFallback = "v";
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        world.Engine.Lock();

        // The user turns "a" back on and makes it the default again; the guard turns it off again, and this time
        // Windows moves the default elsewhere.
        world.Mics.Enable("a");
        world.Mics.UserSetsDefault("a", MicRole.Console);
        world.Mics.DefaultFallback = "v2";
        world.Pump();
        Assert.True(world.Mics["a"].IsOff);
        Assert.Equal(new DefaultMics("v2", "v", "v"), world.Mics.Defaults);

        world.Engine.Unlock();

        Assert.Equal(new DefaultMics("a", "a", "a"), world.Mics.Defaults);
    }

    [Fact]
    public void DefaultMovedAMomentAfterTheTurnOffIsRecordedByTheSafetyPoll()
    {
        // Windows may move the default a moment after the microphone is turned off, after BlindsDown looked.
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.AddVirtual("v");
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        var late = new LateDefaults(world.Mics);
        var engine = new LockEngine(late, world.Journal, world.Pending, world.Clock, () => world.Settings);
        engine.Initialize();
        engine.Lock();
        Assert.Null(world.PersistedJournal!.DefaultsWhileLocked);

        late.CatchUp();
        engine.Reconcile();
        Assert.Equal(new DefaultMics("v", "v", "v"), world.PersistedJournal!.DefaultsWhileLocked);

        // The user then picks another default for one role: that choice is kept.
        world.Mics.AddVirtual("v2");
        world.Mics.UserSetsDefault("v2", MicRole.Communications);
        late.CatchUp();
        engine.Reconcile();
        engine.Unlock();

        Assert.Equal(new DefaultMics("a", "a", "v2"), world.Mics.Defaults);
    }

    [Fact]
    public void DefaultTheUserPicksAwayFromAMicWhoseTurnOffFailedIsKept()
    {
        // "m" refuses to be turned off, so Windows moves the defaults onto it when "a" is turned off.
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.Add("m").SetEnabledThrows = true;
        world.Mics.AddVirtual("v2");
        world.Mics.DefaultFallback = "m";
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Equal(new DefaultMics("m", "m", "m"), world.Mics.Defaults);

        // The user picks another communications microphone while "m" is still on; later "m" can be turned off.
        world.Mics.UserSetsDefault("v2", MicRole.Communications);
        world.Engine.Reconcile();
        world.Mics["m"].SetEnabledThrows = false;
        world.Clock.AdvanceSeconds(60);
        world.Engine.Reconcile();
        Assert.True(world.Mics["m"].IsOff);
        world.Engine.Unlock();

        // The roles the lock moved are put back; the user's choice is kept.
        Assert.Equal(new DefaultMics("a", "a", "v2"), world.Mics.Defaults);
        Assert.DoesNotContain(world.Mics.SetDefaultLog, s => s.Role == MicRole.Communications);
    }

    [Fact]
    public void DefaultTheUserPicksBeforeTheDefaultMicIsTurnedOffIsKept()
    {
        // "a" can't be muted at first, so it isn't turned off either. The user moves every default to "v2", and
        // then "a" is muted and turned off by the same safety poll: that turn-off moved no default.
        LockWorld world = StrongerWorld();
        world.Mics.Add("a").SetMuteThrows = true;
        world.Mics.AddVirtual("v");
        world.Mics.AddVirtual("v2");
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        Assert.Equal(LockState.PartiallyLocked, world.Engine.Lock().State);

        foreach (MicRole role in new[] { MicRole.Console, MicRole.Multimedia, MicRole.Communications })
        {
            world.Mics.UserSetsDefault("v2", role);
        }

        world.Mics["a"].SetMuteThrows = false;
        world.Engine.Reconcile();
        Assert.True(world.Mics["a"].IsOff && world.Mics["a"].Muted);
        world.Engine.Unlock();

        Assert.Equal(new DefaultMics("v2", "v2", "v2"), world.Mics.Defaults);
        Assert.Empty(world.Mics.SetDefaultLog);
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
    }

    [Fact]
    public void DefaultTheLockMovedIsPutBackEvenWhenWindowsMovesItAgain()
    {
        // The lock moves the defaults to the virtual "sonar"; then the app behind "sonar" closes, and Windows moves
        // them on to "voicemeeter". Nobody picked anything: unlock still owes the roles their microphone.
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.AddVirtual("sonar");
        world.Mics.AddVirtual("voicemeeter");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.DefaultFallback = "voicemeeter";
        world.Mics.Unplug("sonar");
        world.Pump();
        world.Engine.Reconcile();
        Assert.Equal(new DefaultMics("voicemeeter", "voicemeeter", "voicemeeter"), world.Mics.Defaults);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new DefaultMics("a", "a", "a"), world.Mics.Defaults);
        Assert.DoesNotContain(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
    }

    [Fact]
    public void DefaultTheUserPicksIsKeptWhenTheOneTheLockLeftGoesAwayLater()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.AddVirtual("sonar");
        world.Mics.AddVirtual("voicemeeter");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("a", "a", "a");
        world.Engine.Initialize();
        world.Engine.Lock();

        // The user picks Voicemeeter for calls; a while later the app behind "sonar" closes.
        world.Mics.UserSetsDefault("voicemeeter", MicRole.Communications);
        world.Engine.Reconcile();
        world.Mics.DefaultFallback = "voicemeeter";
        world.Mics.Unplug("sonar");
        world.Pump();
        world.Engine.Reconcile();
        world.Engine.Unlock();

        Assert.Equal(new DefaultMics("a", "a", "voicemeeter"), world.Mics.Defaults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithEveryMicOffThereIsNoDefaultAndUnlockStillPutsEachRoleBack(bool windowsSwitchesBack)
    {
        // A laptop without virtual microphones: the stronger lock turns every microphone off, so Windows has no
        // default while locked, and gives every role to the first microphone that comes back on.
        LockWorld world = NoVirtualMicWorld();
        world.Mics.ReturningDefaultTakesOver = windowsSwitchesBack;

        world.Engine.Lock();
        Assert.Equal(DefaultMics.None, world.Mics.Defaults);
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new DefaultMics("internal", "internal", "headset"), world.Mics.Defaults);
        Assert.True(report.AllExact, report.Summary);
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithEveryMicOffACrashAnywhereStillPutsEachRoleBack(bool crashDuringUnlock)
    {
        static LockWorld Locked()
        {
            LockWorld world = NoVirtualMicWorld();
            world.Engine.Lock();
            return world;
        }

        Func<LockWorld> setup = crashDuringUnlock ? Locked : NoVirtualMicWorld;
        for (int n = 1; ; n++)
        {
            LockWorld world = setup();
            world.Crash.ArmAt(n);
            bool crashed = false;
            try
            {
                if (crashDuringUnlock)
                {
                    world.Engine.Unlock();
                }
                else
                {
                    world.Engine.Lock();
                }
            }
            catch (SimulatedCrashException)
            {
                crashed = true;
            }

            world.Crash.Disarm();
            string context = crashed ? $"crash at {world.Crash.LastCrash}" : "no crash";
            if (crashed)
            {
                world.Restart();
            }

            world.Engine.Unlock();
            Assert.True(new DefaultMics("internal", "internal", "headset") == world.Mics.Defaults, $"Defaults are {world.Mics.Defaults} after {context}");
            Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), $"{m} after {context}"));
            if (!crashed)
            {
                Assert.True(n > 10, $"Only {n} crash points.");
                break;
            }
        }
    }

    [Fact]
    public void MicsArrivingTogetherWhileLockedAreAllTurnedOffEvenWhenOneBecomesTheDefault()
    {
        // A dock with two microphones is connected while locked, and Windows makes one of them the default. The
        // other is not muted yet when the first one is handled: the default waits for it rather than staying on
        // for the whole lock.
        LockWorld world = StrongerWorld();
        world.Mics.Add("internal");
        world.Mics.AddVirtual("sonar");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("internal", "internal", "internal");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Alerts.Clear();

        world.Mics.Add("dock");
        world.Mics.Add("dock-cam");
        world.Mics.UserSetsDefault("dock", MicRole.Console);
        world.Mics.NotifyEndpoint("dock");
        world.Mics.NotifyEndpoint("dock-cam");
        world.Pump();
        Assert.True(world.Mics["dock"].Muted);
        Assert.Null(world.DeviceStatus("dock").Detail);
        world.Engine.Reconcile();

        Assert.True(world.Mics["dock"].IsOff && world.Mics["dock"].Muted);
        Assert.True(world.Mics["dock-cam"].IsOff && world.Mics["dock-cam"].Muted);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("dock").Protection);
        Assert.Equal(2, world.AlertsOf(EngineAlertKind.NewDeviceLocked).Count);
        world.Engine.Unlock();
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Fact]
    public void HardwareToKeepEnabledIsEmptyWhenUnlocked()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Exclude("a");
        world.Engine.Initialize();

        Assert.Empty(world.Engine.HardwareToKeepEnabled());
    }

    [Fact]
    public void HardwareToKeepEnabledAppliesTheDefaultRuleWithoutTheStrongerLock()
    {
        // The helper's hardware disable moves defaults too, so the rule matters even for a mute-only lock.
        var world = new LockWorld();
        world.Mics.Add("desk");
        world.Mics.Add("webcam");
        world.Mics.Add("headset");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "webcam");
        world.Exclude("headset");
        world.Exclude("sonar");
        world.Engine.Initialize();
        world.Engine.Lock();

        Assert.Equal(
            new[] { "desk", "webcam", "headset", "sonar" }.Select(id => world.Mics[id].HardwareInstanceId!).Order(StringComparer.Ordinal),
            world.Engine.HardwareToKeepEnabled().Order(StringComparer.Ordinal));

        // Without an open microphone, only the excluded ones.
        world.Mics.Unplug("headset");
        world.Mics.Disable("sonar");
        Assert.Equal(
            new[] { "headset", "sonar" }.Select(id => world.Mics[id].HardwareInstanceId!).Order(StringComparer.Ordinal),
            world.Engine.HardwareToKeepEnabled().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SettledDefaultsAreRecordedSoTheHelpersMovesAreUndoneToo()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();

        world.Engine.NoteDefaultsSettled(); // Unlocked: nothing to record.
        Assert.Null(world.PersistedJournal);

        world.Engine.Lock();
        world.Engine.NoteDefaultsSettled(); // Nothing moved.
        Assert.Null(world.PersistedJournal!.DefaultsWhileLocked);

        // The helper disables the USB mic's hardware: its endpoint disappears and Windows moves the default.
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.NoteDefaultsSettled();
        Assert.Equal(new DefaultMics("sonar", "sonar", "sonar"), world.PersistedJournal!.DefaultsWhileLocked);

        // Unlock order: the helper first (the hardware comes back), then the microphones.
        world.Mics.Plug("usb");
        world.Pump();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new DefaultMics("usb", "usb", "usb"), world.Mics.Defaults);
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void HelperMoveThatLandsAfterTheSettledRecordIsStillUndone()
    {
        // Windows moves the default only once the disabled hardware's microphone has disappeared, which can be
        // after the helper answered and the app recorded the settled defaults.
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Engine.NoteDefaultsSettled();
        Assert.Null(world.PersistedJournal!.DefaultsWhileLocked);
        Assert.True(world.PersistedJournal.HelperMayMoveDefaults);

        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Reconcile();
        Assert.Equal(new DefaultMics("sonar", "sonar", "sonar"), world.PersistedJournal!.DefaultsWhileLocked);

        // The user picks another communications microphone later in the lock: that choice is kept.
        world.Mics.AddVirtual("voicemeeter", muted: true);
        world.Mics.UserSetsDefault("voicemeeter", MicRole.Communications);
        world.Engine.Reconcile();

        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Unlock();
        Assert.Equal(new DefaultMics("usb", "usb", "voicemeeter"), world.Mics.Defaults);
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateSettledDefaultsNeverUndoADefaultTheUserPicked(bool safetyPollInBetween)
    {
        // During the lock the user picks headset "y" for calls. Later the helper locks (after a restart while locked,
        // or once it was installed during the lock), disables the USB mic's hardware, and the app notes the defaults.
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.Add("y");
        world.Mics.AddVirtual("sonar");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.UserSetsDefault("y", MicRole.Communications);
        if (safetyPollInBetween)
        {
            world.Engine.Reconcile();
        }

        world.Engine.HardwareToKeepEnabled(); // Right before the helper is asked to lock.
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.NoteDefaultsSettled();
        world.Engine.NoteDefaultsSettled(); // Again: the helper's lock is sent again after a restart.
        Assert.Equal(new DefaultMics("sonar", "sonar", "y"), world.Mics.Defaults);

        // Unlock order: the helper first (the hardware comes back), then the microphones.
        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Unlock();

        Assert.Equal(new DefaultMics("usb", "usb", "y"), world.Mics.Defaults);
        Assert.DoesNotContain(world.Mics.SetDefaultLog, s => s.Role == MicRole.Communications);
    }

    [Fact]
    public void SettledDefaultsThatCantBeReadAreFollowedOnceTheyCanBe()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();

        // The audio service hiccups as the helper answers: the defaults can't be read.
        world.Mics.DefaultsThrow = true;
        world.Engine.NoteDefaultsSettled();
        Assert.True(world.PersistedJournal!.HelperMayMoveDefaults);

        world.Mics.Unplug("usb");
        world.Pump();
        world.Mics.DefaultsThrow = false;
        world.Engine.Reconcile();
        Assert.Equal(new DefaultMics("sonar", "sonar", "sonar"), world.PersistedJournal!.DefaultsWhileLocked);

        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Unlock();
        Assert.Equal(new DefaultMics("usb", "usb", "usb"), world.Mics.Defaults);
    }

    [Fact]
    public void HardwareOfExcludedAndDefaultMicsIsKeptOnWhenWindowsCantListTheMicsRightNow()
    {
        // The user excluded the USB podcast mic for a call. The audio service restarts just as the helper is asked
        // to lock: the last list stands in, so the excluded mic's hardware is not disabled.
        var world = new LockWorld();
        world.Mics.Add("desk");
        world.Mics.Add("podcast");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Exclude("podcast");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.EnumerateThrows = true;
        IReadOnlyList<string> keep = world.Engine.HardwareToKeepEnabled();

        Assert.Equal(
            new[] { "desk", "podcast" }.Select(id => world.Mics[id].HardwareInstanceId!).Order(StringComparer.Ordinal),
            keep.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HardwareToKeepEnabledIsEmptyWhenTheMicsWereNeverListed()
    {
        // Nothing could be listed, so nothing could be muted either: the hardware disable is all the lock has.
        var world = new LockWorld();
        world.Mics.Add("podcast");
        world.Exclude("podcast");
        world.Mics.EnumerateThrows = true;
        world.Engine.Initialize();
        world.Engine.Lock();

        Assert.Empty(world.Engine.HardwareToKeepEnabled());
    }

    [Fact]
    public void HardwareToKeepEnabledIsEmptyWhenTheLockCouldNotListTheMicsEvenIfTheyWereListedBefore()
    {
        // The mics were listed while unlocked, then the audio service fails just as the lock starts: the lock muted
        // nothing, so a list from before it must not keep the default's hardware on (or anything else).
        var world = new LockWorld();
        world.Mics.Add("desk");
        world.Mics.Add("podcast");
        world.Mics.Defaults = new DefaultMics("desk", "desk", "desk");
        world.Exclude("podcast");
        world.Engine.Initialize();
        world.Engine.Reconcile();

        world.Mics.EnumerateThrows = true;
        world.Engine.Lock();

        Assert.False(world.Mics["desk"].Muted);
        Assert.Empty(world.Engine.HardwareToKeepEnabled());
    }

    [Fact]
    public void DefaultOwedToABluetoothMicIsStillGivenBackAfterALockThatTookItOver()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("desk");
        world.Mics.Add("earbuds", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97", unreadableWhenUnplugged: true);
        world.Mics.AddVirtual("sonar");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("earbuds", "earbuds", "earbuds");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("earbuds", EndpointState.Unplugged);
        world.Pump();
        world.Engine.Unlock();
        Assert.Contains(world.Pending.Load(), p => p.DefaultsToRestore == new DefaultMics("sonar", "sonar", "sonar"));

        // Locked again (Win+L) before they reconnect: that lock takes over their pending restore.
        world.Engine.Lock();
        Assert.Empty(world.Pending.Load());
        world.Engine.Unlock();

        world.Mics.Plug("earbuds");
        world.Pump();

        Assert.True(world.Mics["earbuds"].IsOnAndUnmuted());
        Assert.Equal(new DefaultMics("earbuds", "earbuds", "earbuds"), world.Mics.Defaults);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void DefaultOwedToAMicThatReturnsDuringTheNextLockIsGivenBackAtThatUnlock()
    {
        // The helper may move defaults. The default USB headset is unplugged during the lock, so Windows moves the
        // default, and it is still away at unlock. The next lock starts before it returns; it returns during that lock.
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.DefaultFallback = "sonar";
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Engine.NoteDefaultsSettled();
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Reconcile();
        world.Engine.Unlock();
        Assert.Contains(world.Pending.Load(), p => p.DefaultsToRestore is not null);

        world.Engine.Lock();
        world.Engine.NoteDefaultsSettled();
        world.Mics.Plug("usb");
        world.Pump();
        Assert.True(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(new DefaultMics("usb", "usb", "usb"), world.Mics.Defaults);
        Assert.True(world.Mics["usb"].IsOnAndUnmuted());
        Assert.True(report.AllExact, report.Summary);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void WithoutTheHelperADefaultMicLeavingIsNotTheLocksDoing()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Mics.AddVirtual("sonar");
        world.Mics.Defaults = new DefaultMics("usb", "usb", "usb");
        world.Engine.Initialize();
        world.Engine.Lock();

        // The user unplugs it during a mute-only lock and plugs it back in; Windows (here) keeps the new default.
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Reconcile();
        world.Mics.Plug("usb");
        world.Pump();
        world.Engine.Unlock();

        Assert.Empty(world.Mics.SetDefaultLog);
        Assert.Null(world.Journal.Load());
    }

    [Fact]
    public void CrashRightAfterTurningOffTheDefaultStillGetsTheDefaultBack()
    {
        // Killed right after "a" (console default) was turned off, before the journal recorded where Windows
        // moved its default.
        int point = CrashPlan.Find(DefaultsWorld, w => w.Engine.Lock(), "after SetEndpointEnabled(a, False)");
        LockWorld world = DefaultsWorld();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
        Assert.Equal(new DefaultMics("j", "j", "j"), world.Mics.Defaults);
        Assert.Null(world.PersistedJournal!.DefaultsWhileLocked);

        StartupRecovery recovery = world.Restart()!;

        // Recorded at startup, before anything else, so it can't be lost again.
        Assert.Equal(LockPhase.Locking, recovery.Phase);
        Assert.Equal(new DefaultMics("j", "j", "j"), world.PersistedJournal!.DefaultsWhileLocked);
        world.Engine.Unlock();
        Assert.Equal(Original, world.Mics.Defaults);
        Assert.All(world.Mics.All, m => Assert.True(m.IsOnAndUnmuted(), m.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StayingLockedAfterAnUnlockThatPutADefaultBackStillPutsItBackLater(bool crashRightAfterTheTurnOff)
    {
        static LockWorld Setup()
        {
            LockWorld world = StrongerWorld();
            world.Mics.Add("a");
            world.Mics.AddVirtual("v");
            world.Mics.AddVirtual("v2");
            world.Mics.DefaultFallback = "v";
            world.Mics.Defaults = new DefaultMics("a", "a", "a");
            world.Engine.Initialize();
            world.Engine.Lock();
            return world;
        }

        // The unlock is killed right after it set the console default back to "a".
        int point = CrashPlan.Find(Setup, w => w.Engine.Unlock(), "after SetDefaultMic(a, Console)");
        LockWorld world = Setup();
        world.Crash.ArmAt(point);
        Assert.Throws<SimulatedCrashException>(() => world.Engine.Unlock());
        Assert.Equal(new DefaultMics("a", "v", "v"), world.Mics.Defaults);

        // "Stay locked". This time Windows moves the console default somewhere else.
        world.Mics.DefaultFallback = "v2";
        world.Restart();
        if (crashRightAfterTheTurnOff)
        {
            int again = CrashPlan.Find(
                () =>
                {
                    LockWorld probe = Setup();
                    probe.Crash.ArmAt(point);
                    Assert.Throws<SimulatedCrashException>(() => probe.Engine.Unlock());
                    probe.Mics.DefaultFallback = "v2";
                    probe.Restart();
                    return probe;
                },
                w => w.Engine.Lock(),
                "after SetEndpointEnabled(a, False)");
            world.Crash.ArmAt(again);
            Assert.Throws<SimulatedCrashException>(() => world.Engine.Lock());
            world.Restart();
        }

        world.Engine.Lock();
        Assert.True(world.Mics["a"].IsOff);
        Assert.Equal(new DefaultMics("v2", "v", "v"), world.Mics.Defaults);

        world.Engine.Unlock();
        Assert.Equal(new DefaultMics("a", "a", "a"), world.Mics.Defaults);
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
    }

    /// <summary>A backend whose defaults lag behind: it reports them as they were at the last <see cref="CatchUp"/>.</summary>
    private sealed class LateDefaults(FakeMicBackend inner) : IMicBackend
    {
        private DefaultMics _reported = inner.Defaults;

        public void CatchUp() => _reported = inner.Defaults;

        public IReadOnlyList<MicEndpoint> EnumerateMics() => inner.EnumerateMics();

        public DefaultMics GetDefaultMics() => _reported;

        public bool? GetMute(string endpointId) => inner.GetMute(endpointId);

        public void SetMute(string endpointId, bool muted) => inner.SetMute(endpointId, muted);

        public void SetEndpointEnabled(string endpointId, bool enabled) => inner.SetEndpointEnabled(endpointId, enabled);

        public void SetDefaultMic(string endpointId, MicRole role)
        {
            inner.SetDefaultMic(endpointId, role);
            CatchUp();
        }
    }

    /// <summary>
    /// "a" is the console and multimedia default, "j" the communications default, "m" another physical mic and
    /// "v" a virtual one. No fallback is configured, so Windows picks the first active microphone by ID.
    /// </summary>
    private static LockWorld DefaultsWorld()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.Add("j", hardwareMute: true);
        world.Mics.Add("m");
        world.Mics.AddVirtual("v");
        world.Mics.Defaults = Original;
        world.Engine.Initialize();
        return world;
    }

    /// <summary>"internal" is the console and multimedia default, "headset" the communications default, and
    /// "webcam" another physical microphone. No virtual microphone.</summary>
    private static LockWorld NoVirtualMicWorld()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("headset");
        world.Mics.Add("internal");
        world.Mics.Add("webcam");
        world.Mics.Defaults = new DefaultMics("internal", "internal", "headset");
        world.Engine.Initialize();
        return world;
    }
}
