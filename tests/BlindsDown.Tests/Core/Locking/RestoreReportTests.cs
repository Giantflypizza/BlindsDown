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
