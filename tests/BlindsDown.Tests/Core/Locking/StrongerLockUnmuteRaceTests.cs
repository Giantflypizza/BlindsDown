using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;
using static BlindsDown.Tests.Core.Locking.StrongerLockSupport;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// Turning a microphone off doesn't cut the streams apps already have open, so it must never be turned off while it
/// is unmuted: once it is off, nothing can mute it. An app that unmutes it whenever it is muted can do so between the
/// verified mute and the turn-off, and the notification of that only arrives once the engine is done.
/// </summary>
public sealed class StrongerLockUnmuteRaceTests
{
    [Fact]
    public void MicUnmutedBetweenItsMuteAndItsTurnOffIsMutedAgainBeforeItIsTurnedOff()
    {
        LockWorld world = RaceWorld();
        var mutedAtTurnOff = new List<bool>();
        bool unmuted = false;
        world.Crash.Interject = point =>
        {
            // Right after the turn-off was written down, before it happens: the call app unmutes "a".
            if (!unmuted && point.StartsWith("after journal save", StringComparison.Ordinal) && world.TurnOffAction("a") is { Status: ActionStatus.Applying })
            {
                unmuted = true;
                world.Mics.OtherAppSetsMute("a", false);
            }

            if (point == "before SetEndpointEnabled(a, False)")
            {
                mutedAtTurnOff.Add(world.Mics["a"].Muted);
            }
        };

        world.Engine.Lock();
        world.Pump(); // The app's unmute is only handled now, when "a" is off.

        Assert.True(unmuted);
        Assert.NotEmpty(mutedAtTurnOff);
        Assert.All(mutedAtTurnOff, Assert.True);
        Assert.True(world.Mics["a"].IsOff && world.Mics["a"].Muted, world.Mics["a"].ToString());
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("a").Protection);
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Equal("Something unmuted \"Microphone (a)\" while locked. BlindsDown muted it again.", alert.Message);
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));
        Assert.Equal(1, world.MuteAction("a").GuardCount);

        world.Engine.Unlock();
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
    }

    [Fact]
    public void MicUnmutedAsItIsTurnedOffIsTurnedBackOnMutedAndTurnedOffAgain()
    {
        LockWorld world = RaceWorld();
        bool unmuted = false;
        world.Crash.Interject = point =>
        {
            // The last moment: after its mute was checked, as Windows turns it off.
            if (!unmuted && point == "before SetEndpointEnabled(a, False)")
            {
                unmuted = true;
                world.Mics.OtherAppSetsMute("a", false);
            }
        };

        world.Engine.Lock();
        Assert.True(world.Mics["a"].IsOff);
        Assert.False(world.Mics["a"].Muted); // Off and unmuted: its open streams hear everything.

        // The unmute arrives for a microphone that is off: it can't be read, so it is turned back on to check.
        world.Pump();

        Assert.True(world.Mics["a"].IsOff && world.Mics["a"].Muted, world.Mics["a"].ToString());
        Assert.Equal(ActionStatus.Applied, world.TurnOffAction("a")!.Status);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("a").Protection);
        Assert.Single(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Empty(world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked));

        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void MicUnmutedAsItIsTurnedOffThatCantBeTurnedBackOnIsReportedNotSaidToBeMuted()
    {
        LockWorld world = RaceWorld();
        bool unmuted = false;
        world.Crash.Interject = point =>
        {
            if (!unmuted && point == "before SetEndpointEnabled(a, False)")
            {
                unmuted = true;
                world.Mics.OtherAppSetsMute("a", false);
            }
        };

        world.Engine.Lock();
        Assert.True(world.Mics["a"].IsOff && !world.Mics["a"].Muted);

        // Windows refuses to turn it back on, so it can't be muted: nothing looks at it while it is off, so it is said now.
        world.Mics["a"].SetEnabledThrows = true;
        world.Pump();

        Assert.True(world.Mics["a"].IsOff);
        Assert.Empty(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.LockFailed));
        Assert.True(alert.IsUrgent);
        Assert.Contains("may still hear it", alert.Message, StringComparison.Ordinal);
        Assert.Equal(ActionStatus.Applied, world.TurnOffAction("a")!.Status);
        Assert.Equal(DeviceProtection.EndpointDisabled, world.DeviceStatus("a").Protection);

        world.Mics["a"].SetEnabledThrows = false;
        RestoreReport report = world.Engine.Unlock()!;
        Assert.True(world.Mics["a"].IsOnAndUnmuted());
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void MicThatWontStayMutedIsNotTurnedOff()
    {
        LockWorld world = RaceWorld();
        bool fighting = false;
        world.Crash.Interject = point =>
        {
            // Once the turn-off is written down, the app unmutes "a", and again each time BlindsDown mutes it.
            bool turnOffAhead = point.StartsWith("after journal save", StringComparison.Ordinal) && world.TurnOffAction("a") is { Status: ActionStatus.Applying };
            if ((turnOffAhead && !fighting) || (fighting && point == "after SetMute(a, True)"))
            {
                fighting = true;
                world.Mics.OtherAppSetsMute("a", false);
            }
        };

        LockStatusInfo status = world.Engine.Lock();

        Assert.True(fighting);
        Assert.False(world.Mics["a"].IsOff);
        Assert.Equal(0, world.Mics.SetEndpointEnabledCallsFor("a"));
        Assert.Null(world.TurnOffAction("a")); // The write-ahead went: nothing was changed.
        Assert.Equal(LockState.PartiallyLocked, status.State); // The guard keeps muting it.
    }

    [Fact]
    public void UnmuteOfAMicThatDisconnectsBeforeItIsHandledIsNotReportedAsMutedAgain()
    {
        var world = new LockWorld();
        world.Mics.Add("headset", hardware: @"BTHHFENUM\BTHHFPAUDIO\8&2A5E7F&0&97", unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Pump();

        // Something unmutes it, and it disconnects before BlindsDown gets to it: it can't be muted right now.
        world.Mics.OtherAppSetsMute("headset", false);
        world.Mics.Unplug("headset", EndpointState.Unplugged);
        world.Pump();

        Assert.Empty(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
        Assert.Equal(0, world.Action("headset").GuardCount);

        // When it reconnects it is muted, and that is what the user is told.
        world.Mics.Plug("headset");
        world.Pump();
        Assert.True(world.Mics["headset"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        Assert.Empty(world.AlertsOf(EngineAlertKind.UnmutedWhileLocked));
    }

    /// <summary>A stronger-lock world with the physical "a" and the virtual default "v", not locked yet.</summary>
    private static LockWorld RaceWorld()
    {
        LockWorld world = StrongerWorld();
        world.Mics.Add("a");
        world.Mics.AddVirtual("v");
        world.Mics.Defaults = new DefaultMics("v", "v", "v");
        world.Engine.Initialize();
        return world;
    }
}
