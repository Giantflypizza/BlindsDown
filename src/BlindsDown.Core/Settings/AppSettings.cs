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
    /// <summary>Stable identity (survives USB port changes), e.g. "mic|USB\VID_1234&amp;PID_5678&amp;MI_00|MICROPHONE".</summary>
    public required string StableKey { get; init; }

    /// <summary>The exact Windows ID the device had when it was excluded (matched too).</summary>
    public string? LastKnownId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// The Windows IDs of identical devices (same <see cref="StableKey"/>) that were connected when this one was
    /// excluded. The exclusion never follows to one of them when the excluded device is gone: that is its twin, not
    /// the excluded device plugged in somewhere else (see <see cref="ExclusionRules"/>). Null when there were none,
    /// or for exclusions made before BlindsDown recorded them.
    /// </summary>
    public IReadOnlyList<string>? TwinIds { get; init; }

    /// <summary>Compares <see cref="TwinIds"/> by content (a record compares lists by reference).</summary>
    public bool Equals(DeviceExclusion? other) =>
        other is not null
        && StableKey == other.StableKey
        && LastKnownId == other.LastKnownId
        && DisplayName == other.DisplayName
        && (TwinIds ?? []).SequenceEqual(other.TwinIds ?? []);

    public override int GetHashCode() => HashCode.Combine(StableKey, LastKnownId, DisplayName, TwinIds?.Count ?? 0);
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

    /// <summary>
    /// Stronger microphone lock: besides muting, turn physical microphones off in Windows (Sound settings
    /// "Don't allow"), so apps can't open them at all, not even in exclusive mode. Off by default: apps that
    /// have a microphone open lose it (like unplugging it) and some need it picked again after unlocking.
    /// Virtual microphones are only muted (they go silent when the physical ones are off).
    /// </summary>
    public bool DisableMicsInWindows { get; set; }

    /// <summary>Lock cameras too. Needs the optional helper service; without it this has no effect.</summary>
    public bool LockCameras { get; set; } = true;

    /// <summary>Also lock infrared (Windows Hello) cameras. Off by default so face sign-in keeps working.</summary>
    public bool IncludeInfraredCameras { get; set; }

    /// <summary>Also lock virtual cameras (OBS, Phone Link...). Off by default: most of them pass on a real camera, which is
    /// locked anyway. A phone used as a webcam (Phone Link) shows the phone's camera, so locking it needs this.</summary>
    public bool IncludeVirtualCameras { get; set; }

    /// <summary>With the helper: also disable microphone-only hardware (webcam mics, USB mics) at the device
    /// level, which even apps that bypass Windows audio can't get past. Hardware that also has speakers or
    /// headphones is never disabled. Off by default for the same reason as <see cref="DisableMicsInWindows"/>.</summary>
    public bool DisableMicOnlyHardware { get; set; }

    /// <summary>Whether an existing user has been told once that cameras can now be locked.</summary>
    public bool CameraOfferShown { get; set; }

    /// <summary>Lock automatically when Windows is locked (Win+L).</summary>
    public bool AutoLockOnWindowsLock { get; set; }

    /// <summary>Lock automatically when the PC goes to sleep.</summary>
    public bool AutoLockOnSleep { get; set; }

    public Hotkey GetHotkey() =>
        Settings.Hotkey.TryParse(Hotkey, out Hotkey hk) && hk.Validate() == HotkeyProblem.None
            ? hk
            : Settings.Hotkey.Default;

    /// <summary>Whether <paramref name="mic"/> is excluded, given every microphone Windows lists right now
    /// (see <see cref="ExclusionRules"/> for why the other devices matter).</summary>
    public bool IsExcluded(MicEndpoint mic, IEnumerable<MicEndpoint> allMics) => IsExcluded(ExcludedDevices, mic, allMics);

    /// <summary>Whether one of <paramref name="exclusions"/> excludes <paramref name="mic"/>, given every microphone
    /// Windows lists right now.</summary>
    public static bool IsExcluded(IReadOnlyCollection<DeviceExclusion> exclusions, MicEndpoint mic, IEnumerable<MicEndpoint> allMics) =>
        exclusions.Count > 0
        && ExclusionRules.IsExcluded(
            exclusions.Select(e => (e.StableKey, e.LastKnownId, e.TwinIds)),
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

        // A hand-edited file can hold "[null]" or "stableKey": null ("required" only demands that the property is
        // there, not that it has a value). Such an entry can't match anything, and the code that reads it (exclusion
        // matching, the request to the helper, Settings) would trip over it. A missing name is only cosmetic.
        ExcludedDevices = ExcludedDevices
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.StableKey))
            .Select(e => e with { DisplayName = e.DisplayName ?? e.StableKey, TwinIds = WithoutEmpty(e.TwinIds) })
            .ToList();
        AllowedApps = AllowedApps
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.AppKey))
            .Select(a => a.DisplayName is null ? a with { DisplayName = a.AppKey } : a)
            .ToList();
        if (!Settings.Hotkey.TryParse(Hotkey, out Hotkey hk) || hk.Validate() != HotkeyProblem.None)
        {
            Hotkey = Settings.Hotkey.Default.ToString();
        }
    }

    private static List<string>? WithoutEmpty(IReadOnlyList<string>? ids) =>
        ids?.Where(id => !string.IsNullOrEmpty(id)).ToList() is { Count: > 0 } kept ? kept : null;
}
