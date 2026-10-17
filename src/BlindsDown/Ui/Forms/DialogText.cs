using System.Globalization;
using BlindsDown.Core.Locking;

namespace BlindsDown.Ui.Forms;

/// <summary>The popup that summarises an unlock.</summary>
internal sealed record RestorePopup(string Title, string Message, AlertSeverity Severity, bool Sticky, bool OfferDetails);

/// <summary>What the crash-recovery dialog says.</summary>
internal sealed record RecoveryMessage(string Heading, string Text);

/// <summary>Wording for the restore report and the startup / exit dialogs, kept apart from the UI so it is tested.</summary>
internal static class DialogText
{
    public const string RestoreButton = "Restore my devices";
    public const string RestoreNote = "Unmute exactly what BlindsDown muted. Everything else stays as it is.";
    public const string KeepLockedButton = "Keep locked";
    public const string KeepLockedNote = "Your microphones stay muted. Unlock later with your hotkey or the tray icon.";
    public const string UnlockAndExitButton = "Unlock and exit";
    public const string UnlockAndExitNote = "Restore your microphones exactly as they were, then close BlindsDown.";
    public const string ExitLockedButton = "Exit and stay locked";
    public const string ExitLockedNote = "Your microphones stay muted until you start BlindsDown again and unlock.";

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
        if (report.AllExact)
        {
            return new RestorePopup("Microphones unlocked", report.Summary, AlertSeverity.Success, Sticky: false, OfferDetails: false);
        }

        return report.ProblemCount > 0
            ? new RestorePopup("Unlocked – please check your microphones", report.Summary, AlertSeverity.Warning, Sticky: true, OfferDetails: true)
            : new RestorePopup("Microphones unlocked", report.Summary, AlertSeverity.Info, Sticky: true, OfferDetails: true);
    }

    public static RecoveryMessage ForRecovery(StartupRecovery recovery, TimeZoneInfo zone, CultureInfo culture, DateTimeOffset now)
    {
        string heading = recovery switch
        {
            { ExitedCleanly: true } => "BlindsDown was closed while your microphones were locked.",
            { Phase: LockPhase.Locking } => "BlindsDown closed unexpectedly while it was locking your microphones.",
            { Phase: LockPhase.Unlocking } => "BlindsDown closed unexpectedly while it was unlocking your microphones.",
            _ => "BlindsDown closed unexpectedly while your microphones were locked.",
        };

        string state = recovery.DevicesChanged switch
        {
            0 => "No microphones were muted yet.",
            1 => "1 microphone is still muted.",
            _ => recovery.DevicesChanged.ToString(culture) + " microphones are still muted.",
        };

        string since = "Locked since " + ActivityText.Since(recovery.LockedSince, now, zone, culture) + ".";
        return new RecoveryMessage(heading, state + " " + since);
    }
}
