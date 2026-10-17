using BlindsDown.Infrastructure;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// Buttons that open Windows Settings pages. They go through <see cref="SafeShell"/> (the only code
/// allowed to start a process) and never take the app down if Windows refuses.
/// </summary>
internal static class SettingsLinks
{
    public static void OpenSound() => Run(SafeShell.OpenSoundSettings, "Sound settings");

    public static void OpenTaskbar() => Run(SafeShell.OpenTaskbarSettings, "taskbar settings");

    public static void Open(Action open) => Run(open, "Settings");

    private static void Run(Action open, string what)
    {
        try
        {
            open();
        }
        catch (Exception ex)
        {
            // A button click is a UI boundary: log and carry on (Settings may be blocked by policy).
            Log.Error("Could not open " + what, ex);
        }
    }
}
