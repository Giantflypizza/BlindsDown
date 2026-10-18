namespace BlindsDown.Ui.Forms;

/// <summary>The status line of the Settings "Camera" tab: a badge, a short title and one or two sentences.</summary>
internal sealed record HelperStatusLine(AlertSeverity Severity, string Title, string? Text);

/// <summary>How an installed helper's version compares with this BlindsDown's.</summary>
internal enum HelperVersionAge
{
    /// <summary>Either version is missing or can't be compared (a pre-release of the same number, for example).</summary>
    Unknown,

    /// <summary>The helper comes from an older BlindsDown: updating it is the right fix.</summary>
    Older,

    /// <summary>The helper comes from a newer BlindsDown: "updating" it would put this older version in its place.</summary>
    Newer,
}

/// <summary>
/// Wording about the optional camera helper, kept apart from the windows so it is tested. Plain words: the
/// only technical term is "service", and the helper is always "the camera helper".
/// </summary>
internal static class HelperText
{
    public const string EnableButton = "Turn on camera lock…";
    public const string UpdateButton = "Update helper";
    public const string ReplaceButton = "Replace helper";
    public const string RepairButton = "Repair";
    public const string RemoveButton = "Remove helper…";

    public const string IntroHeading = "Lock your cameras too";

    public const string Intro =
        "BlindsDown can also turn your cameras off while locked, so no app can use them, and turn them back on when you unlock.";

    public const string WhyAdmin =
        "Turning a camera off needs administrator rights, so BlindsDown installs a small helper service for it. Windows asks for your permission once. Like BlindsDown itself, the helper never connects to the internet.";

    public const string OptionsApplyNextLock = "You can change these while locked too; they apply the next time you lock.";

    public const string HelperAbout =
        "A small Windows service that turns cameras (and, if you choose, microphone-only hardware) off and on for BlindsDown, and nothing else.";

    /// <summary>Under "Also lock virtual cameras". Phone Link is the case that is NOT covered by locking the real
    /// cameras (the phone's camera is one BlindsDown can't turn off), so it must not sit next to "locked anyway".</summary>
    public const string VirtualCamerasHint =
        "Software cameras. Some only pass on a real camera, which is locked anyway. Others, like a phone used as a webcam with Phone Link, show a camera BlindsDown can't turn off, so they are only locked if you tick this.";

    /// <summary>What the progress line says while an install, update or removal runs.</summary>
    public static string Busy(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "Working on the camera helper… Windows may ask for your permission." : detail;

    /// <summary>
    /// Compares the helper's version with this BlindsDown's ("1.0.0", "1.1.0-beta.2", "1.0.0+abc"): the parts
    /// after "-" or "+" are ignored for the comparison, and two different versions with the same number (two
    /// pre-releases, say) can't be ordered, so they are <see cref="HelperVersionAge.Unknown"/>.
    /// </summary>
    public static HelperVersionAge CompareVersions(string? helperVersion, string? appVersion)
    {
        if (Numeric(helperVersion) is not Version helper || Numeric(appVersion) is not Version app)
        {
            return HelperVersionAge.Unknown;
        }

        int order = helper.CompareTo(app);
        return order < 0 ? HelperVersionAge.Older : order > 0 ? HelperVersionAge.Newer : HelperVersionAge.Unknown;
    }

    /// <summary>
    /// The status line for an installed helper; null when it isn't installed (the tab explains instead).
    /// <paramref name="lockCameras"/> is the "Lock cameras" checkbox: a working helper with it unticked locks no
    /// camera, so the line must not say "Camera lock is on" then. <paramref name="appVersion"/> is this
    /// BlindsDown's version: a helper of another version is only called "older" when it really is.
    /// </summary>
    public static HelperStatusLine? Status(HelperUiState state, bool lockCameras = true, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        string? version = Clean(state.HelperVersion);
        string? app = Clean(appVersion);
        string installed = version is null ? "The installed helper" : $"The installed helper (version {version})";
        string thisApp = app is null ? "this BlindsDown" : $"this BlindsDown (version {app})";
        return state.Availability switch
        {
            HelperAvailability.Working when !lockCameras => new HelperStatusLine(
                AlertSeverity.Info,
                "Camera lock is off",
                (version is null ? "The camera helper is installed" : $"The camera helper (version {version}) is installed")
                    + ", but \"Lock cameras\" is off, so locking leaves your cameras on."),
            HelperAvailability.Working => new HelperStatusLine(
                AlertSeverity.Success,
                "Camera lock is on",
                version is null ? null : $"Camera helper version {version}."),
            HelperAvailability.NeedsUpdate => CompareVersions(version, app) switch
            {
                HelperVersionAge.Older => new HelperStatusLine(
                    AlertSeverity.Warning,
                    "The camera helper needs an update",
                    $"{installed} is older than {thisApp}. Update it so camera lock keeps working as it should. Windows asks for your permission once."),
                HelperVersionAge.Newer => new HelperStatusLine(
                    AlertSeverity.Warning,
                    "The camera helper is from a newer BlindsDown",
                    $"{installed} is newer than {thisApp}. Use the newer BlindsDown, or replace the helper with this version. Windows asks for your permission once."),
                _ => new HelperStatusLine(
                    AlertSeverity.Warning,
                    "The camera helper is from another BlindsDown version",
                    $"{installed} doesn't match {thisApp}. Update it so camera lock works as it should. Windows asks for your permission once."),
            },
            HelperAvailability.NotResponding => new HelperStatusLine(
                AlertSeverity.Danger,
                "The camera helper isn't responding",
                "It is installed, but it doesn't answer, so cameras can't be locked or unlocked right now. Repair installs it again. Windows asks for your permission once."),
            _ => null,
        };
    }

    /// <summary>The Update/Replace/Repair button for a state, or null when it has none. A helper from a newer
    /// BlindsDown is "replaced", never "updated": the button puts this (older) version in its place.</summary>
    public static string? FixButton(HelperAvailability availability, HelperVersionAge age = HelperVersionAge.Unknown) => availability switch
    {
        HelperAvailability.NeedsUpdate => age == HelperVersionAge.Newer ? ReplaceButton : UpdateButton,
        HelperAvailability.NotResponding => RepairButton,
        _ => null,
    };

    private static string? Clean(string? version) => string.IsNullOrWhiteSpace(version) ? null : version.Trim();

    private static Version? Numeric(string? version)
    {
        string? text = Clean(version);
        if (text is null)
        {
            return null;
        }

        int suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        if (!Version.TryParse(text, out Version? parsed))
        {
            return null;
        }

        // "1.0" and "1.0.0" are the same version; Version treats a missing part as smaller.
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }
}
