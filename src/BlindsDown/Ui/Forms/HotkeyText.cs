using BlindsDown.Core.Settings;

namespace BlindsDown.Ui.Forms;

/// <summary>User-facing explanations for <see cref="HotkeyProblem"/>.</summary>
internal static class HotkeyText
{
    public const string Capturing = "Press a key combination…";

    public static string Describe(HotkeyProblem problem) => problem switch
    {
        HotkeyProblem.NoKey => "Choose a hotkey: click the box, then press a key combination.",
        HotkeyProblem.ReservedF12 => "F12 is reserved by Windows. Choose another key.",
        HotkeyProblem.NeedsModifier => "Add Ctrl, Alt or Shift. Only F13–F24 and Pause work on their own.",
        HotkeyProblem.ModifierOnly => "Add a key to go with the modifiers, for example Ctrl+Alt+Shift+M.",
        HotkeyProblem.ShiftWithTypingKey => "Add Ctrl or Alt. Shift plus a letter or number would get in the way of typing.",
        _ => string.Empty,
    };

    /// <summary>Short form shown inside the capture box itself while the user is still typing.</summary>
    public static string ShortHint(HotkeyProblem problem) => problem switch
    {
        HotkeyProblem.ReservedF12 => "F12 is reserved – try another key",
        HotkeyProblem.NeedsModifier => "Add Ctrl, Alt or Shift",
        HotkeyProblem.ShiftWithTypingKey => "Add Ctrl or Alt",
        _ => Capturing,
    };

    /// <summary>
    /// What the capture box says about a pressed combination: null when it can be used, otherwise the
    /// short hint to show instead.
    /// </summary>
    public static string? CaptureProblem(Hotkey hotkey)
    {
        HotkeyProblem problem = hotkey.Validate();
        return problem == HotkeyProblem.None ? null : ShortHint(problem);
    }
}
