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

/// <summary>The choices made in the first-run welcome.</summary>
internal sealed record WelcomeResult(bool StartWithWindows, bool LockCameras);

/// <summary>Small modal dialogs and the restore report presentation.</summary>
internal static class Dialogs
{
    /// <summary>
    /// Shown at startup when BlindsDown did not shut down cleanly while a lock was active (or while it was
    /// locking/unlocking). Explains what happened in plain words ("BlindsDown closed unexpectedly while your
    /// microphones were locked. N microphones are still muted or turned off.", plus the cameras when
    /// <paramref name="camerasLocked"/>: the camera helper holds a lock too) and offers "Keep locked" / "Restore my
    /// devices" (default button: Restore). Topmost, centered. If the dialog cannot be shown at all, the
    /// answer is "Keep locked": nothing changes, and the user can still unlock with the hotkey.
    /// </summary>
    public static RecoveryChoice AskRecovery(StartupRecovery recovery, bool camerasLocked = false)
    {
        RecoveryMessage message = DialogText.ForRecovery(recovery, TimeZoneInfo.Local, CultureInfo.CurrentCulture, DateTimeOffset.UtcNow, camerasLocked);
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
    /// pin the tray icon on Windows 11 (button opens taskbar settings via SafeShell), a "Start with Windows"
    /// checkbox and, when <paramref name="offerCameraLock"/>, an unchecked "Also lock my cameras" checkbox that
    /// says Windows will ask for administrator permission once to install a small helper.
    /// </summary>
    public static WelcomeResult ShowWelcome(string hotkeyText, bool startWithWindowsDefault, bool offerCameraLock)
    {
        using var form = new WelcomeForm(hotkeyText, startWithWindowsDefault, offerCameraLock);
        form.ShowDialog();
        return new WelcomeResult(form.StartWithWindows, form.LockCameras);
    }

    /// <summary>
    /// Before the UAC prompt for the helper: what will be installed (a small service that runs in the
    /// background with administrator rights, in Program Files, used only to turn devices off and on; still no
    /// network), that Windows asks for administrator permission once, and how to remove it later; the technical
    /// details are behind "More details". Returns true for "Install" (Cancel is the default). Centred on
    /// <paramref name="owner"/> (Settings) when given, otherwise on the screen and topmost. The Settings window
    /// asks this itself before it raises its EnableHelperRequested event.
    /// </summary>
    public static bool ConfirmInstallHelper(IWin32Window? owner = null) =>
        Confirm(DialogText.InstallHelper(), TaskDialogIcon.ShieldBlueBar, owner);

    /// <summary>
    /// Before removing the helper: cameras can no longer be locked; when <paramref name="locked"/>, that locked
    /// cameras are turned back on first. Returns true for "Remove" (Cancel is the default). The Settings window
    /// asks this itself before it raises its RemoveHelperRequested event.
    /// </summary>
    public static bool ConfirmRemoveHelper(bool locked, IWin32Window? owner = null) =>
        Confirm(DialogText.RemoveHelper(locked), TaskDialogIcon.Information, owner);

    /// <summary>
    /// Before "Replace helper" puts this BlindsDown's helper in place of one from a newer BlindsDown (an old copy of
    /// the app must not downgrade it unnoticed). Returns true for "Replace" (Cancel is the default). The Settings
    /// window asks this itself before it raises its UpdateHelperRequested event.
    /// </summary>
    public static bool ConfirmReplaceNewerHelper(string? helperVersion, string appVersion, IWin32Window? owner = null) =>
        Confirm(DialogText.ReplaceNewerHelper(helperVersion, appVersion), TaskDialogIcon.ShieldWarningYellowBar, owner);

    /// <summary>The task dialog page behind <see cref="ConfirmInstallHelper"/> and <see cref="ConfirmRemoveHelper"/>.
    /// Its first button goes ahead and carries the UAC shield; the second is Cancel, the default.</summary>
    internal static TaskDialogPage ConfirmPage(ConfirmMessage message, TaskDialogIcon icon)
    {
        var confirm = new TaskDialogButton(message.ConfirmButton) { ShowShieldIcon = true };
        return new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = message.Heading,
            Text = message.Text,
            Icon = icon,
            Buttons = { confirm, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true,
            Expander = new TaskDialogExpander(message.Details)
            {
                CollapsedButtonText = "More details",
                ExpandedButtonText = "Fewer details",
                Position = TaskDialogExpanderPosition.AfterText,
            },
        };
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
    /// true for unlock-and-exit, false for stay-locked. Cancel is the default, so a stray Enter changes nothing.
    /// <paramref name="micsLocked"/> and <paramref name="camerasLocked"/> say what is locked (the camera helper can
    /// hold its lock while the microphones are unlocked), so the question names the right devices.</summary>
    public static bool? AskExitWhileLocked(bool micsLocked = true, bool camerasLocked = false)
    {
        var unlock = new TaskDialogCommandLinkButton(DialogText.UnlockAndExitButton, DialogText.UnlockAndExitNote);
        var stay = new TaskDialogCommandLinkButton(DialogText.ExitLockedButton, DialogText.ExitLockedNote(micsLocked, camerasLocked));
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = DialogText.ExitHeading(micsLocked, camerasLocked),
            Text = DialogText.ExitQuestion,
            Icon = TaskDialogIcon.Warning,
            Buttons = { unlock, stay, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            AllowCancel = true,
        };

        TaskDialogButton? answer = ShowTopmost(page);
        return answer == unlock ? true : answer == stay ? false : null;
    }

    /// <summary>
    /// Asked on Exit when a lock or unlock is still waiting for Windows audio or the camera helper. "Keep running" is the
    /// default (it finishes as soon as they answer). "Exit anyway" leaves it half done; the next start offers to finish or
    /// undo it. Returns true for "Exit anyway".
    /// </summary>
    /// <summary>
    /// Asked at Exit when another program's lock or unlock request is waiting but can't run yet (Windows audio hasn't
    /// answered since BlindsDown started): its sender was told it was delivered. Returns true to exit anyway.
    /// </summary>
    public static bool ConfirmExitDroppingRequests(string what)
    {
        var keep = new TaskDialogCommandLinkButton("Keep running", $"BlindsDown does it as soon as Windows audio answers.");
        var exit = new TaskDialogCommandLinkButton("Exit anyway", $"The request to {what} is not carried out.");
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = $"Another program asked BlindsDown to {what}",
            Text = "Windows audio hasn't answered since BlindsDown started, so BlindsDown couldn't do it yet.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, exit },
            DefaultButton = keep,
            AllowCancel = true,
        };

        return ShowTopmost(page) == exit;
    }

    /// <param name="unlocking">An unlock is stuck (else a lock).</param>
    /// <param name="micsInvolved">The microphones are part of it (their record makes the next start offer recovery);
    /// false when only the camera helper's part is left.</param>
    /// <param name="requestWaiting">Another program's lock or unlock request waits behind it (exiting drops it).</param>
    public static bool ConfirmExitWhileBusy(bool unlocking, bool micsInvolved = true, bool requestWaiting = false)
    {
        var keep = new TaskDialogCommandLinkButton("Keep running", "BlindsDown finishes as soon as Windows audio or the camera helper answers. Try Exit again then.");
        string dropped = requestWaiting ? " A lock or unlock that another program asked for is not carried out." : string.Empty;
        var exit = new TaskDialogCommandLinkButton(
            "Exit anyway",
            (micsInvolved
                ? "The next time BlindsDown starts, it offers to finish or undo what was left half done."
                : "The camera helper finishes on its own once it answers. The next time BlindsDown starts, it shows whether your cameras are still locked.") + dropped);
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = unlocking ? "BlindsDown is still unlocking your devices" : "BlindsDown is still locking your devices",
            Text = "Windows audio or the camera helper hasn't answered for a while. If this lasts, restarting the PC usually clears it.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, exit },
            DefaultButton = keep,
            AllowCancel = true,
        };

        return ShowTopmost(page) == exit;
    }

    /// <summary>
    /// Asked after "Unlock and exit" when the unlock was not exact (a microphone was unplugged and can only
    /// be put back while BlindsDown runs, or an unmute failed) or failed outright. Lists what happened and
    /// returns true to exit anyway, false to keep BlindsDown running (the default). <paramref name="stillLocked"/>:
    /// part of the lock is still held, so the tray icon retries the unlock; when not given, an
    /// <paramref name="error"/> means exactly that (the unlock failed, or the camera helper didn't answer). Once
    /// everything is unlocked a tray click would lock again, so the dialog never suggests it then.
    /// </summary>
    public static bool ConfirmExitAfterImperfectRestore(RestoreReport? report, string? error, bool? stillLocked = null)
    {
        ImperfectExitMessage message = DialogText.ExitAfterImperfectRestore(report, error, stillLocked ?? error is not null);
        var keep = new TaskDialogCommandLinkButton(DialogText.KeepRunningButton, message.KeepNote);
        var exit = new TaskDialogCommandLinkButton(DialogText.ExitAnywayButton, message.ExitNote);
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = message.Heading,
            Text = message.Text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, exit },
            DefaultButton = keep,
            AllowCancel = false,
        };

        return ShowTopmost(page) == exit;
    }

    /// <summary>History was just turned off: also delete what was recorded so far (app use and the lock timeline,
    /// which keeps recording locks and unlocks)? Default: no.</summary>
    public static bool AskDeleteExistingHistory()
    {
        var delete = new TaskDialogButton(DialogText.DeleteHistoryButton);
        var keep = new TaskDialogButton(DialogText.KeepHistoryButton);
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = DialogText.HistoryOffHeading,
            Text = DialogText.HistoryOffText,
            Icon = TaskDialogIcon.Information,
            Buttons = { delete, keep },
            DefaultButton = keep,
            AllowCancel = true,
        };

        return ShowTopmost(page) == delete;
    }

    private static bool Confirm(ConfirmMessage message, TaskDialogIcon icon, IWin32Window? owner)
    {
        TaskDialogPage page = ConfirmPage(message, icon);
        TaskDialogButton confirm = page.Buttons[0];
        TaskDialogButton? answer = owner is null ? ShowTopmost(page) : ShowOwned(page, owner);
        return answer == confirm;
    }

    /// <summary>Shows a task dialog over its owner window (modal to it). Returns null if it could not be shown.</summary>
    private static TaskDialogButton? ShowOwned(TaskDialogPage page, IWin32Window owner)
    {
        try
        {
            return TaskDialog.ShowDialog(owner, page, TaskDialogStartupLocation.CenterOwner);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            // Same failure set as ShowTopmost: a button click must not take the window down.
            Log.Error($"Could not show the dialog \"{page.Heading}\"", ex);
            return null;
        }
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
