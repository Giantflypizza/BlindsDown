namespace BlindsDown.Ui.Forms;

/// <summary>
/// Colours for the owner-drawn parts of the popups and dialogs. Everything is derived from
/// <see cref="SystemColors"/>, which WinForms swaps for dark values when the app runs in dark mode,
/// plus the <see cref="Theme"/> accents. The navy accent is unreadable on a dark background, so it gets
/// a lighter variant there.
/// </summary>
internal static class UiPalette
{
    /// <summary>A lighter navy for dark backgrounds (contrast ~6:1 on the dark surface).</summary>
    private static readonly Color AccentOnDark = Color.FromArgb(138, 168, 226);

    /// <summary>True when the current window background is dark (the app follows Windows' dark mode).</summary>
    public static bool IsDark => SystemColors.Window.GetBrightness() < 0.5f;

    /// <summary>The navy brand accent, adjusted so it stays readable on the current background.</summary>
    public static Color Accent => IsDark ? AccentOnDark : Theme.Accent;

    public static Color SeverityColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Success => Theme.Ok,
        AlertSeverity.Warning => Theme.Warning,
        AlertSeverity.Danger => Theme.Locked,
        _ => Accent,
    };

    /// <summary>Text drawn on top of <see cref="SeverityColor"/>: white, except on amber where white is too faint.</summary>
    public static Color OnSeverityColor(AlertSeverity severity) =>
        severity == AlertSeverity.Warning || (severity == AlertSeverity.Info && IsDark)
            ? Color.FromArgb(28, 28, 32)
            : Color.White;

    /// <summary>Text for error messages next to inputs. Theme red is readable on both light and dark surfaces.</summary>
    public static Color ErrorText => IsDark ? Color.FromArgb(255, 128, 120) : Theme.Locked;

    /// <summary>Mixes <paramref name="color"/> into <paramref name="background"/>; 0 = background, 1 = color.</summary>
    public static Color Blend(Color background, Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(background.R + ((color.R - background.R) * amount)),
            (int)Math.Round(background.G + ((color.G - background.G) * amount)),
            (int)Math.Round(background.B + ((color.B - background.B) * amount)));
    }
}
