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
        Assert.Equal("3 microphones are still muted. Locked since 11:00.", message.Text);
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
        Assert.StartsWith("1 microphone is still muted.", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_WhenNothingWasMutedYet()
    {
        var recovery = new StartupRecovery(LockPhase.Locking, false, Now, DevicesChanged: 0);

        Assert.StartsWith("No microphones were muted yet.", DialogText.ForRecovery(recovery, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Now).Text, StringComparison.Ordinal);
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
