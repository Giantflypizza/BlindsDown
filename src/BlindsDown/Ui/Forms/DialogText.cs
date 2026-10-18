using System.Globalization;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;

namespace BlindsDown.Ui.Forms;

/// <summary>The popup that summarises an unlock.</summary>
internal sealed record RestorePopup(string Title, string Message, AlertSeverity Severity, bool Sticky, bool OfferDetails);

/// <summary>What the crash-recovery dialog says.</summary>
internal sealed record RecoveryMessage(string Heading, string Text);

/// <summary>A question before a step that needs administrator rights: the text, a collapsed "More details"
/// part for the curious, and the label of the button that goes ahead (it carries the UAC shield).</summary>
internal sealed record ConfirmMessage(string Heading, string Text, string Details, string ConfirmButton);

/// <summary>What the question after an imperfect "Unlock and exit" says: the lines, and the notes under its two buttons.</summary>
internal sealed record ImperfectExitMessage(string Heading, string Text, string KeepNote, string ExitNote);

/// <summary>Wording for the restore report and the startup / exit dialogs, kept apart from the UI so it is tested.</summary>
internal static class DialogText
{
    public const string RestoreButton = "Restore my devices";
    public const string RestoreNote = "Undo exactly what BlindsDown changed: unmute, turn back on, put default microphones back. Everything else stays as it is.";
    public const string KeepLockedButton = "Keep locked";
    public const string KeepLockedNote = "Your devices stay locked. Unlock later with your hotkey or the tray icon.";
    public const string UnlockAndExitButton = "Unlock and exit";
    public const string UnlockAndExitNote = "Put your devices back exactly as they were, then close BlindsDown.";
    public const string ExitLockedButton = "Exit and stay locked";
    public const string ExitQuestion = "What should happen to them when BlindsDown closes?";
    public const string KeepRunningButton = "Keep BlindsDown running";
    public const string ExitAnywayButton = "Exit anyway";

    public const string HistoryOffHeading = "History is now off";
    public const string HistoryOffText =
        "BlindsDown will no longer record which apps use your microphone or camera. Locks and unlocks are still recorded in the "
        + "lock timeline, so nothing can unlock without you seeing it. Do you also want to delete the history and lock timeline recorded so far?";

    public const string DeleteHistoryButton = "Delete history and timeline";
    public const string KeepHistoryButton = "Keep it";

    /// <summary>The heading of the "Exit while locked" question, naming what is actually locked. The helper can
    /// hold its camera lock while the microphones are unlocked (it didn't answer an unlock), and the other way round.</summary>
    public static string ExitHeading(bool micsLocked, bool camerasLocked) => (micsLocked, camerasLocked) switch
    {
        (true, true) => "Your microphones and cameras are locked.",
        (false, true) => "Your cameras are locked.",
        _ => "Your microphones are locked.",
    };

    /// <summary>The note under "Exit and stay locked". The camera helper keeps its lock without the app, also
    /// across a restart (it re-applies it when Windows starts).</summary>
    public static string ExitLockedNote(bool micsLocked, bool camerasLocked) => (micsLocked, camerasLocked) switch
    {
        (true, true) => "Your microphones stay locked until you start BlindsDown again and unlock. The camera helper keeps your cameras off, even after a restart.",
        (false, true) => "The camera helper keeps your cameras off, even after a restart, until you start BlindsDown again and unlock.",
        _ => "Your microphones stay locked until you start BlindsDown again and unlock.",
    };

    /// <summary>
    /// The question after "Unlock and exit" did not put everything back. <paramref name="stillLocked"/>: part of
    /// the lock is still held (the unlock failed, or the camera helper didn't answer), so a tray click retries the
    /// unlock. Otherwise the app is unlocked: a tray click would lock again. The app only retries microphones that
    /// are unplugged (pending); the camera helper keeps retrying a camera or microphone hardware it couldn't turn
    /// back on by itself, with or without the app, and its "off" doesn't survive a restart once the lock is gone.
    /// For anything else the user is pointed to where to fix it.
    /// </summary>
    public static ImperfectExitMessage ExitAfterImperfectRestore(RestoreReport? report, string? error, bool stillLocked)
    {
        var lines = new List<string>();
        if (error is not null)
        {
            lines.Add("Unlocking failed: " + error);
        }

        List<RestoreLine> problems = report?.Lines.Where(l => l.Outcome != RestoreOutcome.Exact).ToList() ?? [];
        lines.AddRange(problems.Select(line => $"• {line.DeviceName}: {line.Message}"));

        bool pending = report?.PendingCount > 0;
        bool micProblems = problems.Any(l => l.Kind == DeviceKind.Microphone && l.Outcome != RestoreOutcome.Pending);
        bool otherDevices = problems.Any(l => l.Kind != DeviceKind.Microphone);
        bool helperRetries = problems.Any(l => l.Kind != DeviceKind.Microphone && l.Outcome == RestoreOutcome.Failed);
        string keep = (pending, stillLocked) switch
        {
            (true, true) => "Recommended. Try again later from the tray icon. BlindsDown puts a microphone back as soon as it is plugged back in.",
            (true, false) => "Recommended. BlindsDown puts a microphone back as soon as it is plugged back in.",
            (false, true) => "Try again later from the tray icon.",
            _ when !helperRetries => "BlindsDown stays open, unlocked. It can't retry these by itself.",
            _ when micProblems => "BlindsDown stays open, unlocked. It can't retry the microphones by itself; the camera helper keeps trying the rest.",
            _ => "BlindsDown stays open, unlocked. The camera helper keeps trying to turn these back on by itself.",
        };

        string restart = helperRetries ? " Restarting the PC turns a camera that is still off back on." : string.Empty;
        string exit = stillLocked
            ? "What is still locked stays locked until you start BlindsDown again and unlock."
            : !otherDevices
                ? "Then check these microphones yourself in Windows Sound settings."
                : (micProblems || pending
                    ? "Then check these devices yourself: microphones in Windows Sound settings, cameras and other devices in Device Manager."
                    : "Then check these devices yourself in Device Manager.") + restart;
        return new ImperfectExitMessage("Not everything could be put back yet", string.Join("\n", lines), keep, exit);
    }

    public static string Outcome(RestoreOutcome outcome) => outcome switch
    {
        RestoreOutcome.Exact => "Restored",
        RestoreOutcome.Pending => "Waiting",
        RestoreOutcome.ChangedByOthers => "Changed",
        RestoreOutcome.Failed => "Failed",
        RestoreOutcome.DefaultChanged => "Default changed",
        _ => outcome.ToString(),
    };

    public static AlertSeverity OutcomeSeverity(RestoreOutcome outcome) => outcome switch
    {
        RestoreOutcome.Exact => AlertSeverity.Success,
        RestoreOutcome.Pending => AlertSeverity.Info,
        RestoreOutcome.Failed => AlertSeverity.Danger,
        _ => AlertSeverity.Warning,
    };

    /// <summary>
    /// Exact restores get a quiet success popup. Anything else stays on screen until dismissed and offers
    /// the details: amber when something needs the user's attention, neutral when devices are only
    /// waiting to be reconnected.
    /// </summary>
    public static RestorePopup ForReport(RestoreReport report)
    {
        string unlocked = report.Lines.Any(l => l.Kind == DeviceKind.Camera) ? "Microphones and cameras unlocked" : "Microphones unlocked";
        if (report.AllExact)
        {
            return new RestorePopup(unlocked, report.Summary, AlertSeverity.Success, Sticky: false, OfferDetails: false);
        }

        string check = report.Lines.Any(l => l.Kind != DeviceKind.Microphone) ? "Unlocked – please check your devices" : "Unlocked – please check your microphones";
        return report.ProblemCount > 0
            ? new RestorePopup(check, report.Summary, AlertSeverity.Warning, Sticky: true, OfferDetails: true)
            : new RestorePopup(unlocked, report.Summary, AlertSeverity.Info, Sticky: true, OfferDetails: true);
    }

    /// <summary>Before installing the camera helper (the UAC prompt follows).</summary>
    public static ConfirmMessage InstallHelper() =>
        new(
            "Turn on camera lock?",
            "BlindsDown installs a small helper service that turns your cameras off while BlindsDown is locked, and back on when you unlock. "
                + "Turning devices off needs administrator rights, so Windows asks for your permission once.\n\n"
                + "The helper runs in the background, does nothing else, and never connects to the internet. You can remove it any time in Settings > Camera.",
            $"The helper is a copy of BlindsDown.exe in Program Files, set up as the Windows service \"{HelperProtocol.ServiceDisplayName}\". "
                + "It only accepts \"lock\" and \"unlock\" from programs running on this PC (never over the network), and it picks the devices itself "
                + "(cameras, plus microphone-only hardware if you turn that on), so it can't be used to turn off anything else. "
                + "Another program running as you could ask it to unlock; BlindsDown then shows a red \"Unlocked by another program\" popup (at its next start if it wasn't running). "
                + "While it works, a lock you leave on stays on after a restart. If the helper is ever removed or can't start at all, restarting the PC turns every camera it turned off back on.",
            "Install");

    /// <summary>Before "Replace helper" puts this BlindsDown's (older) helper in place of one from a newer BlindsDown
    /// (the UAC prompt follows). An old copy of the app must not downgrade the helper without the user knowing.</summary>
    public static ConfirmMessage ReplaceNewerHelper(string? helperVersion, string appVersion) =>
        new(
            "Replace the newer camera helper?",
            (string.IsNullOrWhiteSpace(helperVersion) ? "The installed camera helper" : $"The installed camera helper (version {helperVersion.Trim()})")
                + $" comes from a newer BlindsDown than this one (version {appVersion}). If you have that newer BlindsDown, use it instead. "
                + "Replacing puts this older version of the helper in its place. Windows asks for your permission once.",
            "Your lock and your camera options are kept. The newer BlindsDown will offer to update the helper again the next time it runs.",
            "Replace");

    /// <summary>Before removing the camera helper (the UAC prompt follows). <paramref name="locked"/>: BlindsDown is locked right now.</summary>
    public static ConfirmMessage RemoveHelper(bool locked) =>
        new(
            "Remove the camera helper?",
            (locked
                ? "BlindsDown is locked right now, so the helper first turns your cameras (and anything else it turned off) back on; your microphones stay locked. "
                    + "After that, BlindsDown can't turn cameras off any more. "
                : "BlindsDown will no longer be able to turn your cameras off. Locking your microphones works as before. ")
                + "Windows asks for your permission once.",
            $"This stops and deletes the Windows service \"{HelperProtocol.ServiceDisplayName}\" and its files in Program Files. "
                + "Anything it turned off that can't be turned back on comes back when you restart the PC. You can turn camera lock on again any time in Settings > Camera.",
            "Remove");

    /// <summary>
    /// The crash-recovery question. The count covers microphones muted or (with the stronger lock) turned off in
    /// Windows. <paramref name="camerasLocked"/>: the camera helper holds a lock too; "Restore my devices" also
    /// turns those cameras back on, "Keep locked" leaves them off.
    /// </summary>
    public static RecoveryMessage ForRecovery(StartupRecovery recovery, TimeZoneInfo zone, CultureInfo culture, DateTimeOffset now, bool camerasLocked = false)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        string heading = recovery switch
        {
            { ExitedCleanly: true } => "BlindsDown was closed while your microphones were locked.",
            { Phase: LockPhase.Locking } => "BlindsDown closed unexpectedly while it was locking your microphones.",
            { Phase: LockPhase.Unlocking } => "BlindsDown closed unexpectedly while it was unlocking your microphones.",
            _ => "BlindsDown closed unexpectedly while your microphones were locked.",
        };

        string state = recovery.DevicesChanged switch
        {
            0 => "No microphones were muted or turned off yet.",
            1 => "1 microphone is still muted or turned off.",
            _ => recovery.DevicesChanged.ToString(culture) + " microphones are still muted or turned off.",
        };

        string cameras = camerasLocked ? " The camera helper also keeps your cameras off." : string.Empty;
        string since = " Locked since " + ActivityText.Since(recovery.LockedSince, now, zone, culture) + ".";
        return new RecoveryMessage(heading, state + cameras + since);
    }
}
