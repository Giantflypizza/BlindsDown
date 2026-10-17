using Microsoft.Win32;

namespace BlindsDown.Ui;

/// <summary>Shared colours and fonts so every window looks like one app.</summary>
internal static class Theme
{
    /// <summary>Locked: unmistakable red.</summary>
    public static readonly Color Locked = Color.FromArgb(214, 48, 49);

    /// <summary>Partially locked / warnings: amber.</summary>
    public static readonly Color Warning = Color.FromArgb(232, 163, 23);

    /// <summary>Unlocked / success: green.</summary>
    public static readonly Color Ok = Color.FromArgb(39, 174, 96);

    /// <summary>Neutral accent (navy, matches the app icon).</summary>
    public static readonly Color Accent = Color.FromArgb(31, 44, 72);

    public static readonly Color LightSurface = Color.FromArgb(250, 250, 252);
    public static readonly Color DarkSurface = Color.FromArgb(32, 32, 36);
    public static readonly Color LightText = Color.FromArgb(24, 24, 28);
    public static readonly Color DarkText = Color.FromArgb(240, 240, 244);

    public static Font BodyFont => SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

    public static Font TitleFont => new(BodyFont.FontFamily, BodyFont.Size + 2.5f, FontStyle.Bold);

    /// <summary>True when Windows apps use dark mode (Settings > Personalization > Colors).</summary>
    public static bool AppsUseDarkMode() => ReadPersonalize("AppsUseLightTheme") == 0;

    /// <summary>True when the taskbar is dark, so tray icons need light glyphs.</summary>
    public static bool TaskbarIsDark() => ReadPersonalize("SystemUsesLightTheme") != 1;

    public static Color Surface => AppsUseDarkMode() ? DarkSurface : LightSurface;

    public static Color Text => AppsUseDarkMode() ? DarkText : LightText;

    private static int? ReadPersonalize(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(valueName) as int?;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
