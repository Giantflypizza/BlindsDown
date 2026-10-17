using System.Globalization;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Forms;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui;

internal enum RecoveryChoice
{
    KeepLocked,
    Restore,
}

/// <summary>Small modal dialogs and the restore report presentation.</summary>
internal static class Dialogs
{
    /// <summary>
    /// Shown at startup when BlindsDown did not shut down cleanly while a lock was active (or while it was
    /// locking/unlocking). Explains what happened in plain words ("BlindsDown closed unexpectedly while your
    /// microphones were locked. N microphones are still muted.") and offers "Keep locked" / "Restore my
    /// devices" (default button: Restore). Topmost, centered. If the dialog cannot be shown at all, the
    /// answer is "Keep locked": nothing changes, and the user can still unlock with the hotkey.
    /// </summary>
    public static RecoveryChoice AskRecovery(StartupRecovery recovery)
    {
        RecoveryMessage message = DialogText.ForRecovery(recovery, TimeZoneInfo.Local, CultureInfo.CurrentCulture, DateTimeOffset.UtcNow);
        var restore = new TaskDialogCommandLinkButton(DialogText.RestoreButton, DialogText.RestoreNote);
        var keep = new TaskDialogCommandLinkButton(DialogText.KeepLockedButton, DialogText.KeepLockedNote);
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = message.Heading,
            Text = message.Text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { restore, keep },
            DefaultButton = restore,
            AllowCancel = false,
        };

        TaskDialogButton? answer = ShowTopmost(page);
        return answer == restore ? RecoveryChoice.Restore : RecoveryChoice.KeepLocked;
    }

    /// <summary>
    /// After unlock: if <see cref="RestoreReport.AllExact"/>, a small success popup through
    /// <paramref name="popups"/> ("All 3 microphones restored exactly as before."). Otherwise a sticky
    /// popup with a "Details" action that opens a window listing each line (outcome icon, device,
    /// message) with an "Open Sound settings" button: amber when something needs attention, neutral
    /// when devices are only waiting to be reconnected.
    /// </summary>
    public static void ShowRestoreReport(RestoreReport report, AlertPopupManager popups)
    {
        RestorePopup popup = DialogText.ForReport(report);
        IReadOnlyList<AlertAction>? actions = popup.OfferDetails
            ? [new AlertAction("Details", () => RestoreDetailsForm.ShowFor(report))]
            : null;
        popups.Show(new AlertModel(popup.Title, popup.Message, popup.Severity, popup.Sticky, actions));
    }

    /// <summary>
    /// First-run welcome: what BlindsDown does, the hotkey, that it never connects to the internet, how to
    /// pin the tray icon on Windows 11 (button opens taskbar settings via SafeShell), and a
    /// "Start with Windows" checkbox. Returns that checkbox's value.
    /// </summary>
    public static bool ShowWelcome(string hotkeyText, bool startWithWindowsDefault)
    {
        using var form = new WelcomeForm(hotkeyText, startWithWindowsDefault);
        form.ShowDialog();
        return form.StartWithWindows;
    }

    /// <summary>
    /// About: version, one-line pitch, "BlindsDown never connects to the internet." in bold, the honest
    /// limits summary, MIT license, and the repository URL as SELECTABLE TEXT (not a link).
    /// </summary>
    public static void ShowAbout(string version)
    {
        using var form = new AboutForm(version);
        form.ShowDialog();
    }

    /// <summary>Exit while locked: "Unlock and exit" / "Exit and stay locked" / Cancel. Returns null on cancel,
    /// true for unlock-and-exit, false for stay-locked. Cancel is the default, so a stray Enter changes nothing.</summary>
    public static bool? AskExitWhileLocked()
    {
        var unlock = new TaskDialogCommandLinkButton(DialogText.UnlockAndExitButton, DialogText.UnlockAndExitNote);
        var stay = new TaskDialogCommandLinkButton(DialogText.ExitLockedButton, DialogText.ExitLockedNote);
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = "Your microphones are locked.",
            Text = "What should happen to them when BlindsDown closes?",
            Icon = TaskDialogIcon.Warning,
            Buttons = { unlock, stay, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true,
        };

        TaskDialogButton? answer = ShowTopmost(page);
        return answer == unlock ? true : answer == stay ? false : null;
    }

    /// <summary>
    /// Asked after "Unlock and exit" when the unlock was not exact (a microphone was unplugged and can only
    /// be unmuted while BlindsDown runs, or an unmute failed) or failed outright. Lists what happened and
    /// returns true to exit anyway, false to keep BlindsDown running (the default).
    /// </summary>
    public static bool ConfirmExitAfterImperfectRestore(RestoreReport? report, string? error)
    {
        var lines = new List<string>();
        if (error is not null)
        {
            lines.Add("Unlocking failed: " + error);
        }

        foreach (RestoreLine line in report?.Lines.Where(l => l.Outcome != RestoreOutcome.Exact) ?? [])
        {
            lines.Add($"• {line.DeviceName}: {line.Message}");
        }

        bool pending = report?.PendingCount > 0;
        var keep = new TaskDialogCommandLinkButton(
            "Keep BlindsDown running",
            pending ? "Recommended. BlindsDown unmutes the microphone as soon as it is plugged back in." : "Try again later from the tray icon.");
        var exit = new TaskDialogCommandLinkButton(
            "Exit anyway",
            "Then check these microphones yourself in Windows Sound settings.");
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = "Not everything could be put back yet",
            Text = string.Join("\n", lines),
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, exit },
            DefaultButton = keep,
            AllowCancel = false,
        };

        return ShowTopmost(page) == exit;
    }

    /// <summary>History was just turned off: also delete what was recorded so far? Default: no.</summary>
    public static bool AskDeleteExistingHistory()
    {
        var delete = new TaskDialogButton("Delete history");
        var keep = new TaskDialogButton("Keep it");
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = "History is now off",
            Text = "No new entries will be recorded. Do you also want to delete the history recorded so far?",
            Icon = TaskDialogIcon.Information,
            Buttons = { delete, keep },
            DefaultButton = keep,
            AllowCancel = true,
        };

        return ShowTopmost(page) == delete;
    }

    /// <summary>
    /// Shows a task dialog centred on the screen and above every other window. These dialogs come from a
    /// tray app with no window of its own, so without topmost they can open behind the user's apps.
    /// Returns null if the dialog could not be shown.
    /// </summary>
    private static TaskDialogButton? ShowTopmost(TaskDialogPage page)
    {
        page.Created += (_, _) =>
        {
            if (page.BoundDialog is { } dialog)
            {
                PopupNative.BringToTopNoActivate(dialog.Handle);
            }
        };

        try
        {
            return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            // A failed TaskDialogIndirect HRESULT surfaces through Marshal.ThrowExceptionForHR as a
            // COMException (ArgumentException for E_INVALIDARG), not only as a Win32Exception. This runs
            // at startup for crash recovery, where an escaping exception would end the app before it has
            // a tray icon, so every such failure means "not shown".
            Log.Error($"Could not show the dialog \"{page.Heading}\"", ex);
            return null;
        }
    }
}
