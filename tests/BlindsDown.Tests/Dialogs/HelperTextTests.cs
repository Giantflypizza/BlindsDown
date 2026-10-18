using BlindsDown.Core.Locking;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class HelperTextTests
{
    [Fact]
    public void Status_ForEveryInstalledState()
    {
        HelperStatusLine working = HelperText.Status(new HelperUiState(HelperAvailability.Working, " 0.2.0 "))!;
        Assert.Equal(AlertSeverity.Success, working.Severity);
        Assert.Equal("Camera lock is on", working.Title);
        Assert.Equal("Camera helper version 0.2.0.", working.Text);

        HelperStatusLine update = HelperText.Status(new HelperUiState(HelperAvailability.NeedsUpdate, "0.1.9"), appVersion: "0.2.0")!;
        Assert.Equal(AlertSeverity.Warning, update.Severity);
        Assert.Equal("The camera helper needs an update", update.Title);
        Assert.StartsWith("The installed helper (version 0.1.9) is older than this BlindsDown (version 0.2.0). Update it", update.Text, StringComparison.Ordinal);

        HelperStatusLine broken = HelperText.Status(new HelperUiState(HelperAvailability.NotResponding))!;
        Assert.Equal(AlertSeverity.Danger, broken.Severity);
        Assert.Contains("Repair installs it again", broken.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_NeverSaysCameraLockIsOnWhileLockCamerasIsUnticked()
    {
        HelperStatusLine off = HelperText.Status(new HelperUiState(HelperAvailability.Working, "0.2.0"), lockCameras: false)!;
        Assert.Equal("Camera lock is off", off.Title);
        Assert.Equal(AlertSeverity.Info, off.Severity);
        Assert.Equal("The camera helper (version 0.2.0) is installed, but \"Lock cameras\" is off, so locking leaves your cameras on.", off.Text);
        Assert.StartsWith("The camera helper is installed, but", HelperText.Status(new HelperUiState(HelperAvailability.Working), lockCameras: false)!.Text, StringComparison.Ordinal);

        // The other states are about the helper itself, whatever the option.
        Assert.Equal("The camera helper needs an update", HelperText.Status(new HelperUiState(HelperAvailability.NeedsUpdate, "0.1.0"), lockCameras: false, appVersion: "0.2.0")!.Title);
        Assert.Equal("The camera helper isn't responding", HelperText.Status(new HelperUiState(HelperAvailability.NotResponding), lockCameras: false)!.Title);
    }

    [Fact]
    public void Status_WithoutAVersionStillReadsWell()
    {
        Assert.Null(HelperText.Status(new HelperUiState(HelperAvailability.Working))!.Text);

        // Without versions nothing says which one is older (a helper that rejects this app's protocol may not say).
        HelperStatusLine unknown = HelperText.Status(new HelperUiState(HelperAvailability.NeedsUpdate))!;
        Assert.Equal("The camera helper is from another BlindsDown version", unknown.Title);
        Assert.StartsWith("The installed helper doesn't match this BlindsDown. Update it", unknown.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("older", unknown.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("newer", unknown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_AHelperFromANewerBlindsDownIsNeverCalledOlder()
    {
        HelperStatusLine newer = HelperText.Status(new HelperUiState(HelperAvailability.NeedsUpdate, "1.1.0"), appVersion: "1.0.0")!;

        Assert.Equal(AlertSeverity.Warning, newer.Severity);
        Assert.Equal("The camera helper is from a newer BlindsDown", newer.Title);
        Assert.StartsWith("The installed helper (version 1.1.0) is newer than this BlindsDown (version 1.0.0).", newer.Text, StringComparison.Ordinal);
        Assert.Contains("replace the helper with this version", newer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("older", newer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Update it", newer.Text, StringComparison.Ordinal);
        Assert.Equal("Replace helper", HelperText.FixButton(HelperAvailability.NeedsUpdate, HelperVersionAge.Newer));
        Assert.Equal("Update helper", HelperText.FixButton(HelperAvailability.NeedsUpdate, HelperVersionAge.Older));
        Assert.Equal("Repair", HelperText.FixButton(HelperAvailability.NotResponding, HelperVersionAge.Newer));
    }

    // HelperVersionAge is internal, so the public theory takes it as an int.
    [Theory]
    [InlineData("0.9.0", "1.0.0", (int)HelperVersionAge.Older)]
    [InlineData("1.0.0", "1.0.1", (int)HelperVersionAge.Older)]
    [InlineData("1.10.0", "1.9.0", (int)HelperVersionAge.Newer)]
    [InlineData("1.1.0-beta.2", "1.0.0", (int)HelperVersionAge.Newer)]
    [InlineData("1.0.0+abc123", "1.0.1", (int)HelperVersionAge.Older)]
    [InlineData("1.0", "1.0.0", (int)HelperVersionAge.Unknown)]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.2", (int)HelperVersionAge.Unknown)]
    [InlineData("not a version", "1.0.0", (int)HelperVersionAge.Unknown)]
    [InlineData(null, "1.0.0", (int)HelperVersionAge.Unknown)]
    [InlineData(" 2.0.0 ", "1.0.0", (int)HelperVersionAge.Newer)]
    public void CompareVersions_OrdersByNumberOnly(string? helper, string app, int expected) =>
        Assert.Equal((HelperVersionAge)expected, HelperText.CompareVersions(helper, app));

    [Fact]
    public void ReplaceConfirmation_SaysTheHelperIsNewerAndWhatReplacingDoes()
    {
        ConfirmMessage message = DialogText.ReplaceNewerHelper("1.1.0", "1.0.0");

        Assert.Equal("Replace", message.ConfirmButton);
        Assert.Contains("(version 1.1.0) comes from a newer BlindsDown than this one (version 1.0.0)", message.Text, StringComparison.Ordinal);
        Assert.Contains("this older version", message.Text, StringComparison.Ordinal);
        Assert.Contains("Windows asks for your permission once", message.Text, StringComparison.Ordinal);
        Assert.StartsWith("The installed camera helper comes from", DialogText.ReplaceNewerHelper(null, "1.0.0").Text, StringComparison.Ordinal);
    }

    // HelperAvailability is internal, so the public theories take it as an int.
    [Theory]
    [InlineData((int)HelperAvailability.NotInstalled)]
    [InlineData((int)HelperAvailability.Busy)]
    public void Status_IsOnlyForAnInstalledHelper(int availability) =>
        Assert.Null(HelperText.Status(new HelperUiState((HelperAvailability)availability)));

    [Theory]
    [InlineData((int)HelperAvailability.NeedsUpdate, "Update helper")]
    [InlineData((int)HelperAvailability.NotResponding, "Repair")]
    [InlineData((int)HelperAvailability.Working, null)]
    [InlineData((int)HelperAvailability.NotInstalled, null)]
    public void FixButton_OnlyWhenSomethingNeedsFixing(int availability, string? expected) =>
        Assert.Equal(expected, HelperText.FixButton((HelperAvailability)availability));

    [Fact]
    public void Busy_ShowsTheDetailOrAPlainDefault()
    {
        Assert.Equal("Removing the camera helper…", HelperText.Busy("Removing the camera helper…"));
        Assert.Contains("Windows may ask for your permission", HelperText.Busy(null), StringComparison.Ordinal);
        Assert.Contains("Windows may ask for your permission", HelperText.Busy("  "), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIntroExplainsAdminRightsTheOnePromptAndNoInternet()
    {
        Assert.Contains("administrator rights", HelperText.WhyAdmin, StringComparison.Ordinal);
        Assert.Contains("Windows asks for your permission once", HelperText.WhyAdmin, StringComparison.Ordinal);
        Assert.Contains("never connects to the internet", HelperText.WhyAdmin, StringComparison.Ordinal);
        Assert.Equal("Turn on camera lock…", HelperText.EnableButton);
    }

    [Fact]
    public void InstallConfirmation_SaysWhatHowAndHowToUndo()
    {
        ConfirmMessage message = DialogText.InstallHelper();

        Assert.Equal("Install", message.ConfirmButton);
        Assert.Contains("Windows asks for your permission once", message.Text, StringComparison.Ordinal);
        Assert.Contains("never connects to the internet", message.Text, StringComparison.Ordinal);
        Assert.Contains("Settings > Camera", message.Text, StringComparison.Ordinal);
        Assert.Contains("Program Files", message.Details, StringComparison.Ordinal);
        Assert.Contains("BlindsDown camera helper", message.Details, StringComparison.Ordinal);
        Assert.Contains("restart", message.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallDetails_DontOverstateWhoCanUseTheHelperOrWhatARestartDoes()
    {
        string details = DialogText.InstallHelper().Details;

        // Any program running as the user can talk to the helper; only the network is shut out.
        Assert.DoesNotContain("from BlindsDown", details, StringComparison.Ordinal);
        Assert.Contains("from programs running on this PC (never over the network)", details, StringComparison.Ordinal);
        Assert.Contains("microphone-only hardware", details, StringComparison.Ordinal);

        // While it works the helper re-applies a lock at boot: a restart only brings cameras back once it is gone.
        Assert.DoesNotContain("always comes back on when you restart", details, StringComparison.Ordinal);
        Assert.Contains("a lock you leave on stays on after a restart", details, StringComparison.Ordinal);
        Assert.Contains("If the helper is ever removed or can't start at all, restarting the PC", details, StringComparison.Ordinal);

        // The red popup comes from the tray app; the helper's change log lets it report a change made while it was closed.
        Assert.Contains("BlindsDown then shows a red \"Unlocked by another program\" popup (at its next start if it wasn't running)", details, StringComparison.Ordinal);
    }

    [Fact]
    public void NotResponding_SaysTheCamerasCantBeUnlockedEither()
    {
        // The "Cameras may still be off" popup leads here: a helper that doesn't answer can't unlock either.
        string text = HelperText.Status(new HelperUiState(HelperAvailability.NotResponding))!.Text!;

        Assert.Contains("can't be locked or unlocked right now", text, StringComparison.Ordinal);
        Assert.Contains("Repair", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperAbout_NamesMicrophoneHardwareToo() =>
        Assert.Contains("microphone-only hardware", HelperText.HelperAbout, StringComparison.Ordinal);

    [Fact]
    public void VirtualCameraHint_NeverCallsAPhoneCameraLockedAnyway()
    {
        string[] sentences = HelperText.VirtualCamerasHint.Split(". ");
        string phone = Assert.Single(sentences, s => s.Contains("Phone Link", StringComparison.Ordinal));
        Assert.DoesNotContain("locked anyway", phone, StringComparison.Ordinal);
        Assert.Contains("only locked if you tick this", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveConfirmation_SaysLockedCamerasComeBackFirst()
    {
        ConfirmMessage unlocked = DialogText.RemoveHelper(locked: false);
        ConfirmMessage locked = DialogText.RemoveHelper(locked: true);

        Assert.Equal("Remove", unlocked.ConfirmButton);
        Assert.DoesNotContain("back on", unlocked.Text, StringComparison.Ordinal);
        Assert.StartsWith("BlindsDown will no longer be able to turn your cameras off.", unlocked.Text, StringComparison.Ordinal);
        Assert.StartsWith("BlindsDown is locked right now, so the helper first turns your cameras", locked.Text, StringComparison.Ordinal);
        Assert.Contains("back on", locked.Text, StringComparison.Ordinal);
        Assert.All([unlocked, locked], m => Assert.Contains("Windows asks for your permission once", m.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void ConfirmPage_GoesAheadOnlyWithTheShieldedButtonAndDefaultsToCancel()
    {
        TaskDialogPage page = BlindsDown.Ui.Dialogs.ConfirmPage(DialogText.InstallHelper(), TaskDialogIcon.ShieldBlueBar);

        Assert.Equal(2, page.Buttons.Count);
        Assert.Equal("Install", page.Buttons[0].Text);
        Assert.True(page.Buttons[0].ShowShieldIcon);
        Assert.True(page.Buttons[1] == TaskDialogButton.Cancel);
        Assert.True(page.DefaultButton == TaskDialogButton.Cancel);
        Assert.True(page.AllowCancel);
        Assert.False(page.Expander!.Expanded);
        Assert.Equal("More details", page.Expander.CollapsedButtonText);
    }

    [Fact]
    public void RestoreReport_TitleMentionsCamerasOnlyWhenTheUnlockCoveredThem()
    {
        var micsOnly = new RestoreReport([new RestoreLine(RestoreOutcome.Exact, "Mic", "Unmuted.")], 1);
        var withCamera = new RestoreReport([new RestoreLine(RestoreOutcome.Exact, "Mic", "Unmuted."), new RestoreLine(RestoreOutcome.Exact, "Cam", "On.", DeviceKind.Camera)], 2, CoversCameras: true);
        var cameraProblem = new RestoreReport([new RestoreLine(RestoreOutcome.Failed, "Cam", "Still off.", DeviceKind.Camera)], 1, CoversCameras: true);
        var hardwareOnly = new RestoreReport([new RestoreLine(RestoreOutcome.Exact, "Webcam audio", "On.", DeviceKind.MicrophoneHardware)], 1, CoversCameras: true);

        Assert.Equal("Microphones unlocked", DialogText.ForReport(micsOnly).Title);
        Assert.Equal("Microphones and cameras unlocked", DialogText.ForReport(withCamera).Title);
        Assert.Equal("All 2 devices restored exactly as before.", DialogText.ForReport(withCamera).Message);
        Assert.Equal("Unlocked – please check your devices", DialogText.ForReport(cameraProblem).Title);
        Assert.Equal("Microphones unlocked", DialogText.ForReport(hardwareOnly).Title);
    }

    [Fact]
    public void RestoreDetails_GroupsByKindAndSaysARestartBringsDevicesBack()
    {
        Assert.Equal("Microphones", RestoreDetailsForm.KindHeading(DeviceKind.Microphone));
        Assert.Equal("Cameras", RestoreDetailsForm.KindHeading(DeviceKind.Camera));
        Assert.Equal("Microphone hardware", RestoreDetailsForm.KindHeading(DeviceKind.MicrophoneHardware));

        var mics = new RestoreReport([new RestoreLine(RestoreOutcome.Failed, "Mic", "Still muted.")], 1);
        var cameras = new RestoreReport([new RestoreLine(RestoreOutcome.Failed, "Cam", "Still off.", DeviceKind.Camera)], 1, CoversCameras: true);
        Assert.DoesNotContain("restart", RestoreDetailsForm.ClosingHint(mics), StringComparison.Ordinal);
        Assert.Contains("comes back on when you restart the PC", RestoreDetailsForm.ClosingHint(cameras), StringComparison.Ordinal);
    }
}
