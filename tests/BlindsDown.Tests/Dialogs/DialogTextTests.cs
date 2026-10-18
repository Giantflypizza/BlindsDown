using System.Globalization;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class DialogTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactRestore_IsAQuietSuccessPopup()
    {
        var report = new RestoreReport([Line(RestoreOutcome.Exact), Line(RestoreOutcome.Exact), Line(RestoreOutcome.Exact)], 3);

        RestorePopup popup = DialogText.ForReport(report);

        Assert.Equal(AlertSeverity.Success, popup.Severity);
        Assert.False(popup.Sticky);
        Assert.False(popup.OfferDetails);
        Assert.Equal("All 3 microphones restored exactly as before.", popup.Message);
    }

    [Fact]
    public void ProblemRestore_IsAStickyWarningWithDetails()
    {
        var report = new RestoreReport([Line(RestoreOutcome.Exact), Line(RestoreOutcome.Failed)], 2);

        RestorePopup popup = DialogText.ForReport(report);

        Assert.Equal(AlertSeverity.Warning, popup.Severity);
        Assert.True(popup.Sticky);
        Assert.True(popup.OfferDetails);
        Assert.Equal(report.Summary, popup.Message);
    }

    [Fact]
    public void PendingOnlyRestore_StaysVisibleButIsNotAWarning()
    {
        var report = new RestoreReport([Line(RestoreOutcome.Exact), Line(RestoreOutcome.Pending)], 2);

        RestorePopup popup = DialogText.ForReport(report);

        Assert.Equal(AlertSeverity.Info, popup.Severity);
        Assert.True(popup.Sticky);
        Assert.True(popup.OfferDetails);
    }

    // AlertSeverity is internal, so the public theory takes it as an int.
    [Theory]
    [InlineData(RestoreOutcome.Exact, (int)AlertSeverity.Success)]
    [InlineData(RestoreOutcome.Pending, (int)AlertSeverity.Info)]
    [InlineData(RestoreOutcome.ChangedByOthers, (int)AlertSeverity.Warning)]
    [InlineData(RestoreOutcome.DefaultChanged, (int)AlertSeverity.Warning)]
    [InlineData(RestoreOutcome.Failed, (int)AlertSeverity.Danger)]
    public void EveryOutcomeHasASeverityAndAWord(RestoreOutcome outcome, int severity)
    {
        Assert.Equal((AlertSeverity)severity, DialogText.OutcomeSeverity(outcome));
        Assert.False(string.IsNullOrWhiteSpace(DialogText.Outcome(outcome)));
    }

    [Fact]
    public void Recovery_AfterACrashWhileLocked_SaysHowManyAreStillMuted()
    {
        var recovery = new StartupRecovery(LockPhase.Locked, ExitedCleanly: false, Now.AddHours(-1), DevicesChanged: 3);

        RecoveryMessage message = DialogText.ForRecovery(recovery, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Now);

        Assert.Equal("BlindsDown closed unexpectedly while your microphones were locked.", message.Heading);
        Assert.Equal("3 microphones are still muted or turned off. Locked since 11:00.", message.Text);
    }

    [Fact]
    public void Recovery_NamesTheCamerasWhenTheHelperHoldsALockToo()
    {
        var recovery = new StartupRecovery(LockPhase.Locked, ExitedCleanly: false, Now.AddHours(-1), DevicesChanged: 2);

        RecoveryMessage message = DialogText.ForRecovery(recovery, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Now, camerasLocked: true);

        Assert.Equal("2 microphones are still muted or turned off. The camera helper also keeps your cameras off. Locked since 11:00.", message.Text);
    }

    [Fact]
    public void RecoveryAndExitChoices_AreAboutEveryDeviceNotJustMutedMicrophones()
    {
        // "Restore" also turns stronger-lock microphones and the helper's cameras back on, and puts defaults back.
        Assert.Contains("unmute, turn back on, put default microphones back", DialogText.RestoreNote, StringComparison.Ordinal);
        Assert.StartsWith("Your devices stay locked.", DialogText.KeepLockedNote, StringComparison.Ordinal);
        Assert.StartsWith("Put your devices back exactly as they were", DialogText.UnlockAndExitNote, StringComparison.Ordinal);
        Assert.All(
            [DialogText.RestoreNote, DialogText.KeepLockedNote, DialogText.UnlockAndExitNote, DialogText.ExitLockedNote(true, true), DialogText.ExitLockedNote(true, false)],
            note => Assert.DoesNotContain("stay muted", note, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, "Your microphones are locked.")]
    [InlineData(false, true, "Your cameras are locked.")]
    [InlineData(true, true, "Your microphones and cameras are locked.")]
    public void Exit_HeadingNamesWhatIsLocked(bool mics, bool cameras, string heading) =>
        Assert.Equal(heading, DialogText.ExitHeading(mics, cameras));

    [Fact]
    public void Exit_StayLockedSaysTheHelperKeepsTheCamerasOff()
    {
        Assert.Equal("Your microphones stay locked until you start BlindsDown again and unlock.", DialogText.ExitLockedNote(true, false));
        Assert.DoesNotContain("microphones", DialogText.ExitLockedNote(false, true), StringComparison.Ordinal);
        Assert.Contains("keeps your cameras off, even after a restart", DialogText.ExitLockedNote(false, true), StringComparison.Ordinal);
        Assert.Contains("keeps your cameras off, even after a restart", DialogText.ExitLockedNote(true, true), StringComparison.Ordinal);
        Assert.Contains("microphones stay locked", DialogText.ExitLockedNote(true, true), StringComparison.Ordinal);
    }

    [Fact]
    public void ImperfectExit_AFailedUnmuteAfterACompleteUnlockIsNotSentToTheTrayIcon()
    {
        // The unlock ran, so BlindsDown is unlocked: a tray click would lock again instead of retrying the unmute.
        var report = new RestoreReport([Line(RestoreOutcome.Exact), new RestoreLine(RestoreOutcome.Failed, "USB Mic", "Unmute did not take effect.")], 2);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.Equal("• USB Mic: Unmute did not take effect.", message.Text);
        Assert.DoesNotContain("tray icon", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("can't retry these", message.KeepNote, StringComparison.Ordinal);
        Assert.Equal("Then check these microphones yourself in Windows Sound settings.", message.ExitNote);
    }

    [Fact]
    public void ImperfectExit_WhenTheUnlockFailedTheTrayIconRetriesIt()
    {
        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(null, "The camera helper didn't answer.", stillLocked: true);

        Assert.Equal("Unlocking failed: The camera helper didn't answer.", message.Text);
        Assert.Equal("Try again later from the tray icon.", message.KeepNote);
        Assert.DoesNotContain("Sound settings", message.ExitNote, StringComparison.Ordinal);
        Assert.Contains("stays locked until you start BlindsDown again and unlock", message.ExitNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ImperfectExit_AnUnpluggedMicrophoneIsWorthWaitingFor()
    {
        var report = new RestoreReport([Line(RestoreOutcome.Exact), new RestoreLine(RestoreOutcome.Pending, "Headset", "Not connected right now.")], 2);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.StartsWith("Recommended.", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("plugged back in", message.KeepNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ImperfectExit_ACameraProblemIsNotSentToSoundSettingsAlone()
    {
        var report = new RestoreReport(
            [Line(RestoreOutcome.Exact), new RestoreLine(RestoreOutcome.Failed, "HD Webcam C270", "Could not be turned back on.", DeviceKind.Camera)],
            2,
            CoversCameras: true);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.Contains("Device Manager", message.ExitNote, StringComparison.Ordinal);
        Assert.Contains("Restarting the PC turns a camera that is still off back on", message.ExitNote, StringComparison.Ordinal);
        Assert.DoesNotContain("check these microphones", message.ExitNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ImperfectExit_ACameraTheHelperCouldNotTurnBackOnIsStillRetriedByTheHelper()
    {
        // The helper keeps retrying a camera it couldn't turn back on (with or without the app), so the note must not
        // say nothing retries it, and a camera-only problem has nothing to do with Sound settings.
        var report = new RestoreReport(
            [Line(RestoreOutcome.Exact), new RestoreLine(RestoreOutcome.Failed, "HD Webcam C270", "Could not be turned back on.", DeviceKind.Camera)],
            2,
            CoversCameras: true);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.DoesNotContain("can't retry", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("camera helper keeps trying", message.KeepNote, StringComparison.Ordinal);
        Assert.DoesNotContain("tray icon", message.KeepNote, StringComparison.Ordinal);
        Assert.Equal("Then check these devices yourself in Device Manager. Restarting the PC turns a camera that is still off back on.", message.ExitNote);
    }

    [Fact]
    public void ImperfectExit_FailedMicrophonesAndCamerasEachGetTheirOwnWay()
    {
        var report = new RestoreReport(
            [
                new RestoreLine(RestoreOutcome.Failed, "USB Mic", "Unmute did not take effect."),
                new RestoreLine(RestoreOutcome.Failed, "HD Webcam C270", "Could not be turned back on.", DeviceKind.Camera),
            ],
            2,
            CoversCameras: true);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.Contains("can't retry the microphones", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("camera helper keeps trying", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("microphones in Windows Sound settings", message.ExitNote, StringComparison.Ordinal);
        Assert.Contains("Device Manager", message.ExitNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ImperfectExit_ACameraChangedByOthersGetsNoRestartPromise()
    {
        // BlindsDown didn't leave it off, so nothing says a restart turns it on (Device Manager's own disable persists).
        var report = new RestoreReport(
            [new RestoreLine(RestoreOutcome.ChangedByOthers, "HD Webcam C270", "Was on before the lock but is off now.", DeviceKind.Camera)],
            1,
            CoversCameras: true);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error: null, stillLocked: false);

        Assert.DoesNotContain("Restart", message.ExitNote, StringComparison.Ordinal);
        Assert.DoesNotContain("keeps trying", message.KeepNote, StringComparison.Ordinal);
        Assert.Equal("Then check these devices yourself in Device Manager.", message.ExitNote);
    }

    [Fact]
    public void ImperfectExit_AMicrophoneBehindHardwareTheHelperStillHoldsNeedsTheUnlockRetried()
    {
        // The helper didn't answer: its microphone hardware is still off, so the microphone behind it is "not
        // connected" (pending). It only comes back once a tray click retries the helper's unlock.
        var report = new RestoreReport([new RestoreLine(RestoreOutcome.Pending, "USB Mic", "Not connected right now.")], 1);

        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, "The camera helper didn't answer.", stillLocked: true);

        Assert.StartsWith("Recommended.", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("Try again later from the tray icon.", message.KeepNote, StringComparison.Ordinal);
        Assert.Contains("plugged back in", message.KeepNote, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryOff_SaysTheLockTimelineKeepsRecording()
    {
        Assert.DoesNotContain("No new entries", DialogText.HistoryOffText, StringComparison.Ordinal);
        Assert.Contains("no longer record which apps use your microphone or camera", DialogText.HistoryOffText, StringComparison.Ordinal);
        Assert.Contains("Locks and unlocks are still recorded in the lock timeline", DialogText.HistoryOffText, StringComparison.Ordinal);
        Assert.Contains("history and lock timeline recorded so far", DialogText.HistoryOffText, StringComparison.Ordinal);
        Assert.Equal("Delete history and timeline", DialogText.DeleteHistoryButton);
    }

    [Theory]
    [InlineData(LockPhase.Locking, false, "BlindsDown closed unexpectedly while it was locking your microphones.")]
    [InlineData(LockPhase.Unlocking, false, "BlindsDown closed unexpectedly while it was unlocking your microphones.")]
    [InlineData(LockPhase.Locked, true, "BlindsDown was closed while your microphones were locked.")]
    public void Recovery_HeadingDependsOnWhatWasHappening(LockPhase phase, bool clean, string heading)
    {
        var recovery = new StartupRecovery(phase, clean, Now.AddMinutes(-5), DevicesChanged: 1);

        RecoveryMessage message = DialogText.ForRecovery(recovery, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Now);

        Assert.Equal(heading, message.Heading);
        Assert.StartsWith("1 microphone is still muted or turned off.", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_WhenNothingWasMutedYet()
    {
        var recovery = new StartupRecovery(LockPhase.Locking, false, Now, DevicesChanged: 0);

        Assert.StartsWith("No microphones were muted or turned off yet.", DialogText.ForRecovery(recovery, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Now).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutText_KeepsTheExactPromiseAndAddress()
    {
        Assert.Equal("BlindsDown never connects to the internet.", AboutForm.NoInternetSentence);
        Assert.Equal("https://github.com/Giantflypizza/BlindsDown", AboutForm.RepositoryUrl);
    }

    [Theory]
    [InlineData(HotkeyProblem.NoKey)]
    [InlineData(HotkeyProblem.ReservedF12)]
    [InlineData(HotkeyProblem.NeedsModifier)]
    [InlineData(HotkeyProblem.ModifierOnly)]
    public void EveryHotkeyProblemIsExplained(HotkeyProblem problem)
    {
        Assert.False(string.IsNullOrWhiteSpace(HotkeyText.Describe(problem)));
        Assert.False(string.IsNullOrWhiteSpace(HotkeyText.ShortHint(problem)));
    }

    [Fact]
    public void NoProblemNeedsNoExplanation() => Assert.Equal(string.Empty, HotkeyText.Describe(HotkeyProblem.None));

    private static RestoreLine Line(RestoreOutcome outcome) => new(outcome, "Mic", "Message.");
}
