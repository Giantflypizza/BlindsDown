using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

public sealed class RestoreReportTests
{
    private static readonly DateTimeOffset Now = FakeClock.Epoch;

    [Fact]
    public void ExactRestoreOfMutedAndUnmutedMics()
    {
        LockJournal journal = Journal(
            Action("a", ActionStatus.Reverted, wasMuted: false),
            Action("b", ActionStatus.Reverted, wasMuted: true));

        RestoreReport report = RestoreReport.Build(journal, After(Mic("a", false), Mic("b", true)));

        Assert.True(report.AllExact);
        Assert.Equal(2, report.DeviceCount);
        Assert.Equal(0, report.PendingCount);
        Assert.Equal(0, report.ProblemCount);
        Assert.Equal("All 2 microphones restored exactly as before.", report.Summary);
        Assert.Equal("Unmuted, as before the lock.", report.Lines[0].Message);
        Assert.Equal("Muted, as before the lock.", report.Lines[1].Message);
        Assert.Equal("Microphone (a)", report.Lines[0].DeviceName);
    }

    [Theory]
    [InlineData(0, "Nothing needed restoring.")]
    [InlineData(1, "1 microphone restored exactly as before.")]
    [InlineData(3, "All 3 microphones restored exactly as before.")]
    public void SummaryWhenEverythingIsExact(int count, string expected)
    {
        LockAction[] actions = Enumerable.Range(0, count).Select(i => Action("m" + i, ActionStatus.Reverted, wasMuted: false)).ToArray();
        MicEndpoint[] mics = Enumerable.Range(0, count).Select(i => Mic("m" + i, false)).ToArray();

        Assert.Equal(expected, RestoreReport.Build(Journal(actions), After(mics)).Summary);
    }

    [Fact]
    public void PendingDevicesAreReportedAsPending()
    {
        LockJournal journal = Journal(
            Action("a", ActionStatus.Reverted, wasMuted: false),
            Action("usb", ActionStatus.RevertPending, wasMuted: false));

        RestoreReport report = RestoreReport.Build(journal, After(Mic("a", false), Mic("usb", null, EndpointState.NotPresent)));

        Assert.False(report.AllExact);
        Assert.Equal(1, report.PendingCount);
        Assert.Equal(0, report.ProblemCount);
        RestoreLine line = report.Lines[1];
        Assert.Equal(RestoreOutcome.Pending, line.Outcome);
        Assert.Contains("unmuted when it is plugged back in", line.Message, StringComparison.Ordinal);
        Assert.Equal("1 restored exactly, 1 will be restored when reconnected.", report.Summary);
    }

    [Fact]
    public void DeviceThatIsAwayButNeedsNothingIsNotPending()
    {
        // Never changed by BlindsDown (could not be opened during the lock), or already muted before it:
        // nothing will happen when it comes back, so it must not be reported as pending.
        LockJournal journal = Journal(
            Action("hfp", ActionStatus.Reverted, wasMuted: false),
            Action("muted-usb", ActionStatus.Reverted, wasMuted: true));

        RestoreReport report = RestoreReport.Build(
            journal,
            After(Mic("hfp", null, EndpointState.Unplugged), Mic("muted-usb", null, EndpointState.NotPresent)));

        Assert.Equal(0, report.PendingCount);
        Assert.True(report.AllExact, report.Summary);
        Assert.All(report.Lines, l => Assert.StartsWith("Not connected right now", l.Message, StringComparison.Ordinal));
        Assert.Contains("left muted", report.Lines[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRestoreNeedsAttention()
    {
        LockAction failed = Action("a", ActionStatus.RevertFailed, wasMuted: false);
        failed.Note = "Access is denied.";

        RestoreReport report = RestoreReport.Build(Journal(failed), After(Mic("a", true)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Equal("Could not be unmuted: Access is denied. Unmute it in Windows Sound settings.", line.Message);
        Assert.Equal(1, report.ProblemCount);
        Assert.Equal("1 needs attention.", report.Summary);
    }

    [Fact]
    public void FailedRestoreWithoutANoteSaysUnknownError()
    {
        RestoreReport report = RestoreReport.Build(Journal(Action("a", ActionStatus.RevertFailed, wasMuted: false)), After(Mic("a", true)));

        Assert.Contains("unknown error", report.Lines[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "Was unmuted before the lock but is muted now")]
    [InlineData(true, false, "Was muted before the lock but is unmuted now")]
    public void ChangedByOthers(bool wasMuted, bool nowMuted, string message)
    {
        RestoreReport report = RestoreReport.Build(
            Journal(Action("a", ActionStatus.Reverted, wasMuted), Action("b", ActionStatus.Reverted, false), Action("c", ActionStatus.Reverted, false)),
            After(Mic("a", nowMuted), Mic("b", false), Mic("c", false)));

        Assert.Equal(RestoreOutcome.ChangedByOthers, report.Lines[0].Outcome);
        Assert.StartsWith(message, report.Lines[0].Message, StringComparison.Ordinal);
        Assert.Equal("2 restored exactly, 1 needs attention.", report.Summary);
    }

    [Fact]
    public void DefaultDeviceChangesAreReported()
    {
        LockJournal journal = Journal(Action("a", ActionStatus.Reverted, false), Action("b", ActionStatus.Reverted, false));
        journal.Before = new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("a", "a", "a"), Now);

        RestoreReport report = RestoreReport.Build(
            journal,
            new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("b", "b", "A"), Now));

        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
        Assert.Equal("The default microphone changed from \"Microphone (a)\" to \"Microphone (b)\".", line.Message);
        Assert.Equal("Microphone (b)", line.DeviceName);
        Assert.Equal(1, report.ProblemCount);
        Assert.Equal("2 restored exactly, 1 needs attention.", report.Summary);
    }

    [Fact]
    public void DefaultDeviceThatDisappearedOrIsUnknown()
    {
        LockJournal journal = Journal();
        journal.Before = new AudioSnapshot([Mic("a", false)], new DefaultMics("a", "a", "ghost"), Now);

        RestoreReport report = RestoreReport.Build(journal, new AudioSnapshot([], DefaultMics.None, Now));

        Assert.Equal(2, report.Lines.Count);
        Assert.Equal("The default microphone changed from \"Microphone (a)\" to \"none\".", report.Lines[0].Message);
        Assert.Equal("The default communications microphone changed from \"an unknown microphone\" to \"none\".", report.Lines[1].Message);
        Assert.Equal("2 need attention.", report.Summary);
    }

    [Fact]
    public void JournalWithoutSnapshotStillReports()
    {
        LockJournal journal = Journal(Action("a", ActionStatus.Reverted, false));
        journal.Before = null;

        RestoreReport report = RestoreReport.Build(journal, After(Mic("a", false)));

        Assert.True(report.AllExact);
    }

    [Fact]
    public void MessagesNeverDoubleTheFullStop()
    {
        LockAction failed = Action("a", ActionStatus.RevertFailed, false);
        failed.Note = "The device is not available.";

        RestoreReport report = RestoreReport.Build(Journal(failed), After(Mic("a", true)));

        Assert.DoesNotContain("..", report.Lines[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, "Turned back on and unmuted, as before the lock.")]
    [InlineData(true, null, "Turned back on and left muted, as before the lock.")]
    [InlineData(false, "It was on already; nothing to turn back on.", "Unmuted, as before the lock.")]
    public void MicThatWasMutedAndTurnedOffHasOneLine(bool wasMuted, string? turnOffNote, string expected)
    {
        LockAction turnOff = TurnOff("a", ActionStatus.Reverted);
        turnOff.Note = turnOffNote;

        RestoreReport report = RestoreReport.Build(Journal(Action("a", ActionStatus.Reverted, wasMuted), turnOff), After(Mic("a", wasMuted)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Exact, line.Outcome);
        Assert.Equal(expected, line.Message);
        Assert.Equal("Microphone (a)", line.DeviceName);
        Assert.Equal(1, report.DeviceCount);
        Assert.Equal("1 microphone restored exactly as before.", report.Summary);
    }

    [Fact]
    public void DeviceCountCountsMicrophonesNotChanges()
    {
        LockJournal journal = Journal(
            Action("a", ActionStatus.Reverted, false),
            Action("b", ActionStatus.Reverted, false),
            TurnOff("a", ActionStatus.Reverted),
            TurnOff("b", ActionStatus.Reverted));

        RestoreReport report = RestoreReport.Build(journal, After(Mic("a", false), Mic("b", false)));

        Assert.Equal(2, report.DeviceCount);
        Assert.Equal(["Microphone (a)", "Microphone (b)"], report.Lines.Select(l => l.DeviceName));
        Assert.Equal("All 2 microphones restored exactly as before.", report.Summary);
    }

    [Fact]
    public void FailedTurnOnNeedsAttentionWhateverTheMute()
    {
        LockAction turnOff = TurnOff("a", ActionStatus.RevertFailed);
        turnOff.Note = "Access is denied.";

        RestoreReport report = RestoreReport.Build(
            Journal(Action("a", ActionStatus.RevertPending, false), turnOff),
            After(Mic("a", null, EndpointState.Disabled)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Equal("Could not be turned back on: Access is denied. Turn it on in Windows Sound settings.", line.Message);
        Assert.Equal("1 needs attention.", report.Summary);
    }

    [Theory]
    [InlineData(ActionStatus.RevertPending, false, "Not connected right now. It will be turned back on and unmuted when it is plugged back in.")]
    [InlineData(ActionStatus.Reverted, true, "Not connected right now. It will be turned back on when it is plugged back in.")]
    public void MicAwayAtUnlockIsPendingForBoth(ActionStatus mute, bool wasMuted, string expected)
    {
        RestoreReport report = RestoreReport.Build(
            Journal(Action("usb", mute, wasMuted), TurnOff("usb", ActionStatus.RevertPending)),
            After(Mic("usb", null, EndpointState.NotPresent)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Pending, line.Outcome);
        Assert.Equal(expected, line.Message);
        Assert.Equal(1, report.PendingCount);
    }

    [Fact]
    public void MicTurnedBackOnButAwayIsPendingForTheUnmute()
    {
        RestoreReport report = RestoreReport.Build(
            Journal(Action("hfp", ActionStatus.RevertPending, false), TurnOff("hfp", ActionStatus.Reverted)),
            After(Mic("hfp", null, EndpointState.Unplugged)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Pending, line.Outcome);
        Assert.Equal("Turned back on, but not connected right now. It will be unmuted when it is plugged back in.", line.Message);
    }

    [Fact]
    public void MicTurnedBackOnButNotUnmutedSaysBoth()
    {
        LockAction mute = Action("a", ActionStatus.RevertFailed, false);
        mute.Note = "Unmute did not take effect";

        RestoreReport report = RestoreReport.Build(Journal(mute, TurnOff("a", ActionStatus.Reverted)), After(Mic("a", true)));

        RestoreLine line = Assert.Single(report.Lines);
        Assert.Equal(RestoreOutcome.Failed, line.Outcome);
        Assert.Equal("Turned back on, but could not be unmuted: Unmute did not take effect. Unmute it in Windows Sound settings.", line.Message);
    }

    [Fact]
    public void DefaultsSetBackByTheUnlockAreNotMentioned()
    {
        LockJournal journal = Journal(Action("a", ActionStatus.Reverted, false), TurnOff("a", ActionStatus.Reverted));
        journal.Before = new AudioSnapshot([Mic("a", false)], new DefaultMics("a", "a", "a"), Now);
        journal.DefaultsWhileLocked = new DefaultMics("v", "v", "v");

        RestoreReport report = RestoreReport.Build(journal, new AudioSnapshot([Mic("a", false)], new DefaultMics("a", "a", "a"), Now));

        Assert.True(report.AllExact, report.Summary);
        Assert.Single(report.Lines);
    }

    [Fact]
    public void TurnOffWithoutAMuteEntryStillGetsALine()
    {
        // Only a damaged or hand-edited journal has one; the report must not trip over it.
        RestoreReport report = RestoreReport.Build(Journal(TurnOff("a", ActionStatus.Reverted)), After(Mic("a", false)));

        Assert.Equal("Turned back on, as before the lock.", Assert.Single(report.Lines).Message);
        Assert.Equal(1, report.DeviceCount);
    }

    [Fact]
    public void MultimediaDefaultThatChangedOnItsOwnGetsItsOwnLine()
    {
        LockJournal journal = Journal(Action("a", ActionStatus.Reverted, false), Action("b", ActionStatus.Reverted, false));
        journal.Before = new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("a", "a", "a"), Now);

        RestoreReport report = RestoreReport.Build(
            journal,
            new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("a", "b", "a"), Now));

        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
        Assert.Equal("The default multimedia microphone changed from \"Microphone (a)\" to \"Microphone (b)\".", line.Message);
    }

    [Fact]
    public void ConsoleAndMultimediaDefaultsThatChangedTogetherGetOneLine()
    {
        LockJournal journal = Journal(Action("a", ActionStatus.Reverted, false), Action("b", ActionStatus.Reverted, false));
        journal.Before = new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("a", "a", "a"), Now);

        RestoreReport report = RestoreReport.Build(
            journal,
            new AudioSnapshot([Mic("a", false), Mic("b", false)], new DefaultMics("b", "b", "a"), Now));

        RestoreLine line = Assert.Single(report.Lines, l => l.Outcome == RestoreOutcome.DefaultChanged);
        Assert.Equal("The default microphone changed from \"Microphone (a)\" to \"Microphone (b)\".", line.Message);
    }

    [Fact]
    public void MicSomeoneTurnedOffDuringTheLockIsNotCalledUnplugged()
    {
        // Muted by the lock, then turned off in Sound settings: it can't be unmuted until it is turned back on.
        RestoreReport owed = RestoreReport.Build(
            Journal(Action("a", ActionStatus.RevertPending, false)),
            After(Mic("a", null, EndpointState.Disabled)));

        RestoreLine pending = Assert.Single(owed.Lines);
        Assert.Equal(RestoreOutcome.Pending, pending.Outcome);
        Assert.Equal("Turned off in Windows right now (not by BlindsDown). It will be unmuted when it is turned back on.", pending.Message);

        // Already muted before the lock, so nothing is owed, but it was on and isn't now.
        RestoreReport changed = RestoreReport.Build(
            Journal(Action("b", ActionStatus.Reverted, true), TurnOff("b", ActionStatus.Reverted)),
            After(Mic("b", null, EndpointState.Disabled)));

        RestoreLine line = Assert.Single(changed.Lines);
        Assert.Equal(RestoreOutcome.ChangedByOthers, line.Outcome);
        Assert.Equal("Was on before the lock but is turned off in Windows now (changed by another app or by you).", line.Message);
    }

    private static LockAction TurnOff(string id, ActionStatus status) => new()
    {
        Kind = ActionKind.DisableMicEndpoint,
        TargetId = id,
        DisplayName = $"Microphone ({id})",
        Status = status,
    };

    private static LockJournal Journal(params LockAction[] actions) => new()
    {
        LockId = Guid.NewGuid(),
        Phase = LockPhase.Unlocking,
        StartedAt = Now,
        Before = new AudioSnapshot([], DefaultMics.None, Now),
        Actions = [.. actions],
    };

    private static LockAction Action(string id, ActionStatus status, bool wasMuted) => new()
    {
        Kind = ActionKind.MuteMic,
        TargetId = id,
        DisplayName = $"Microphone ({id})",
        WasAlreadyLocked = wasMuted,
        Status = status,
    };

    private static MicEndpoint Mic(string id, bool? muted, EndpointState state = EndpointState.Active) => new()
    {
        Id = id,
        Name = $"Microphone ({id})",
        State = state,
        Muted = muted,
    };

    private static AudioSnapshot After(params MicEndpoint[] mics) => new(mics, DefaultMics.None, Now);
}
