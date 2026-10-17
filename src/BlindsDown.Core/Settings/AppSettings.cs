using BlindsDown.Core.Devices;

namespace BlindsDown.Core.Settings;

public enum OverlayCorner
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
}

/// <summary>A device the user excluded from the lock.</summary>
public sealed record DeviceExclusion
{
    /// <summary>Stable identity (survives USB port changes), e.g. "mic|USB\VID_1038&amp;PID_12E0&amp;MI_00|MICROPHONE".</summary>
    public required string StableKey { get; init; }

    /// <summary>The exact Windows ID the device had when it was excluded (matched too).</summary>
    public string? LastKnownId { get; init; }

    public required string DisplayName { get; init; }
}

/// <summary>An app whose mic/camera use should not pop up alerts (it is still recorded in history).</summary>
public sealed record AllowedApp
{
    public required string AppKey { get; init; }

    public required string DisplayName { get; init; }
}

/// <summary>User settings, stored as JSON in %LocalAppData%\BlindsDown\settings.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public string Hotkey { get; set; } = Settings.Hotkey.Default.ToString();

    public bool PlaySounds { get; set; } = true;

    public bool ShowOverlay { get; set; } = true;

    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;

    /// <summary>Thin red lines along every screen edge while locked.</summary>
    public bool ShowScreenEdges { get; set; }

    /// <summary>Keeps the lock indicator out of screen shares and recordings.</summary>
    public bool HideOverlayFromScreenCapture { get; set; } = true;

    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Also pop up a notice when an app stops using the mic/camera.</summary>
    public bool AlertOnStop { get; set; }

    public bool HistoryEnabled { get; set; } = true;

    public int HistoryRetentionDays { get; set; } = 30;

    public List<DeviceExclusion> ExcludedDevices { get; set; } = [];

    public List<AllowedApp> AllowedApps { get; set; } = [];

    /// <summary>Whether the first-run welcome has been shown.</summary>
    public bool FirstRunDone { get; set; }

    public Hotkey GetHotkey() =>
        Settings.Hotkey.TryParse(Hotkey, out Hotkey hk) && hk.Validate() == HotkeyProblem.None
            ? hk
            : Settings.Hotkey.Default;

    /// <summary>Whether <paramref name="mic"/> is excluded, given every microphone Windows lists right now
    /// (see <see cref="ExclusionRules"/> for why the other devices matter).</summary>
    public bool IsExcluded(MicEndpoint mic, IEnumerable<MicEndpoint> allMics) =>
        ExcludedDevices.Count > 0
        && ExclusionRules.IsExcluded(
            ExcludedDevices.Select(e => (e.StableKey, e.LastKnownId)),
            mic.Id,
            mic.StableKey,
            allMics.Where(m => m.State != EndpointState.NotPresent).Select(m => (m.Id, m.StableKey)).ToList());

    public bool IsAppAllowed(string appKey) =>
        AllowedApps.Exists(a => string.Equals(a.AppKey, appKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Repairs out-of-range values from a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650);

        // The JSON enum converter also accepts numbers, so "overlayCorner": 7 loads as an undefined corner.
        if (!Enum.IsDefined(OverlayCorner))
        {
            OverlayCorner = OverlayCorner.TopRight;
        }

        ExcludedDevices ??= [];
        AllowedApps ??= [];

        // "[null]" in a hand-edited file deserializes to a null entry, which IsExcluded would trip over.
        ExcludedDevices.RemoveAll(e => e is null);
        AllowedApps.RemoveAll(a => a is null);
        if (!Settings.Hotkey.TryParse(Hotkey, out Hotkey hk) || hk.Validate() != HotkeyProblem.None)
        {
            Hotkey = Settings.Hotkey.Default.ToString();
        }
    }
}
