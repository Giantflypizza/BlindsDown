using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// The options that decide what the next lock does with each device, so the Devices tab can say it while
/// unlocked ("Turned off while locked", "Left on: infrared cameras aren't locked"). Taken from the controls
/// the user is editing, so the list follows unsaved changes.
/// </summary>
/// <param name="HelperAvailable">The camera helper is installed and usable (cameras can be locked at all).</param>
internal sealed record LockPreview(
    bool HelperAvailable,
    bool LockCameras,
    bool IncludeInfrared,
    bool IncludeVirtual,
    bool DisableMicHardware,
    bool DisableMicsInWindows)
{
    public static LockPreview From(AppSettings settings, bool helperAvailable) =>
        new(
            helperAvailable,
            settings.LockCameras,
            settings.IncludeInfraredCameras,
            settings.IncludeVirtualCameras,
            settings.DisableMicOnlyHardware,
            settings.DisableMicsInWindows);
}

/// <summary>Plain-language descriptions of devices for the Settings "Devices" tab.</summary>
internal static class DeviceText
{
    public const string Empty = "–";

    public const string NeedsHelper = "Not locked: needs the camera helper";

    /// <summary>
    /// What the lock does for one device. While locked that is its real state. While unlocked it is what the
    /// next lock would do with the options in <paramref name="preview"/>, and <paramref name="excluded"/> (the
    /// row's checkbox) wins over the saved exclusion, so the text follows the user's unsaved changes.
    /// States that don't depend on the options (disabled by the user, not connected, a problem) always show.
    /// </summary>
    public static string Protection(DeviceLockInfo device, bool locked, LockPreview preview, bool excluded)
    {
        if (locked)
        {
            string text = device.Protection == DeviceProtection.None && string.IsNullOrWhiteSpace(device.Problem)
                ? NotLockedReason(device)
                : Protection(device.Protection, device.Problem);

            // A remark about a protected device, e.g. why a default microphone was only muted, not turned off.
            return string.IsNullOrWhiteSpace(device.Detail) ? text : $"{text}. {device.Detail}";
        }

        if (device.Protection is DeviceProtection.DisabledByUser or DeviceProtection.NotPresent || !string.IsNullOrWhiteSpace(device.Problem))
        {
            return Protection(device.Protection, device.Problem);
        }

        return excluded ? Protection(DeviceProtection.Excluded) : Preview(device, preview);
    }

    /// <summary>What the lock currently does for one device. A failed lock shows its reason.</summary>
    public static string Protection(DeviceProtection protection, string? problem = null) => protection switch
    {
        DeviceProtection.HardwareMute => "Muted in hardware",
        DeviceProtection.SoftwareMute => "Muted in software (exclusive-mode apps can bypass)",
        DeviceProtection.EndpointDisabled => "Turned off in Windows (apps can't open it)",
        DeviceProtection.DeviceDisabled => "Turned off (device disabled)",
        DeviceProtection.DisabledByUser => "Disabled in Windows – left alone",
        DeviceProtection.NotPresent => "Not connected – locked when it appears",
        DeviceProtection.Excluded => "Excluded",
        DeviceProtection.NeedsHelper => NeedsHelper,
        _ when !string.IsNullOrWhiteSpace(problem) => "Not locked: " + problem,
        _ => Empty,
    };

    /// <summary>"Virtual" devices are software (Sonar, VB-Cable, ...): locking the physical mic behind them already silences them.</summary>
    public static string Type(bool isVirtual) => isVirtual ? "Virtual" : "Physical";

    /// <summary>The Type column: physical or virtual, and for cameras what they see (infrared ones are for Windows Hello).</summary>
    public static string Type(DeviceLockInfo device) =>
        device.Kind == DeviceKind.Camera && device.Camera is CameraType camera ? Type(camera) : Type(device.IsVirtual);

    public static string Type(CameraType camera) => camera switch
    {
        CameraType.Infrared => "Infrared",
        CameraType.ColorAndInfrared => "Colour + infrared",
        CameraType.Virtual => "Virtual",
        _ => "Colour",
    };

    /// <summary>The Kind column. "Mic hardware" is the device behind microphones that has no speakers (a webcam's or USB mic's audio part).</summary>
    public static string Kind(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => "Camera",
        DeviceKind.MicrophoneHardware => "Mic hardware",
        _ => "Microphone",
    };

    /// <summary>Unlocked: what the next lock would do with a device that isn't excluded.</summary>
    private static string Preview(DeviceLockInfo device, LockPreview preview)
    {
        switch (device.Kind)
        {
            case DeviceKind.Camera:
                if (!preview.HelperAvailable || device.Protection == DeviceProtection.NeedsHelper)
                {
                    return NeedsHelper;
                }

                if (!preview.LockCameras)
                {
                    return "Left on: camera lock is off";
                }

                if (device.Camera == CameraType.Infrared && !preview.IncludeInfrared)
                {
                    return "Left on: infrared cameras aren't locked";
                }

                if ((device.Camera == CameraType.Virtual || device.IsVirtual) && !preview.IncludeVirtual)
                {
                    return "Left on: virtual cameras aren't locked";
                }

                // One device with colour and infrared: it is locked as a colour camera, so face sign-in goes with it.
                return device.Camera == CameraType.ColorAndInfrared
                    ? "Turned off while locked (Windows Hello too)"
                    : "Turned off while locked";

            case DeviceKind.MicrophoneHardware:
                if (!preview.HelperAvailable || device.Protection == DeviceProtection.NeedsHelper)
                {
                    return "Left on: needs the camera helper";
                }

                return preview.DisableMicHardware ? "Turned off while locked" : "Left on (its microphones are locked)";

            default:
                // Virtual microphones are only ever muted: they go silent with the physical ones.
                if (preview.DisableMicsInWindows && !device.IsVirtual)
                {
                    return "Muted and turned off in Windows while locked";
                }

                return device.HasHardwareMute ? "Can mute in hardware" : "Software mute (exclusive-mode apps can bypass)";
        }
    }

    /// <summary>Locked, and the lock left this device alone without a problem: say why where the kind tells.</summary>
    private static string NotLockedReason(DeviceLockInfo device) => device.Kind switch
    {
        DeviceKind.Camera when device.Camera == CameraType.Infrared => "Left on: infrared camera",
        DeviceKind.Camera when device.Camera == CameraType.Virtual || device.IsVirtual => "Left on: virtual camera",
        DeviceKind.Camera => "Left on",
        DeviceKind.MicrophoneHardware => "Left on (its microphones are locked)",
        _ => Empty,
    };
}

/// <summary>
/// Matching and rebuilding <see cref="AppSettings.ExcludedDevices"/> with the same rule the lock uses
/// (<see cref="ExclusionRules"/>): an exclusion belongs to the exact device it was made for, and follows
/// it to a new USB port only when no identical twin is connected. Microphones, cameras and microphone
/// hardware are matched separately: an exclusion only ever applies to devices of its own kind (the kind is
/// the prefix of its stable key: "mic|", "cam|" or "hw|"), and twins are only counted among that kind.
/// </summary>
internal static class DeviceExclusions
{
    /// <summary>The kind an exclusion was made for, or null for a key without a known prefix (then it can match any kind).</summary>
    public static DeviceKind? KindOf(DeviceExclusion exclusion)
    {
        string key = exclusion.StableKey;
        int bar = key.IndexOf('|', StringComparison.Ordinal);
        string prefix = bar < 0 ? string.Empty : key[..bar];
        return prefix.ToUpperInvariant() switch
        {
            "MIC" => DeviceKind.Microphone,
            "CAM" => DeviceKind.Camera,
            "HW" => DeviceKind.MicrophoneHardware,
            _ => null,
        };
    }

    public static bool Matches(DeviceExclusion exclusion, DeviceLockInfo device, IReadOnlyCollection<DeviceLockInfo> devices) =>
        IsExcluded([exclusion], device, devices);

    public static bool IsExcluded(IEnumerable<DeviceExclusion> exclusions, DeviceLockInfo device, IReadOnlyCollection<DeviceLockInfo> devices)
    {
        var ofKind = exclusions.Where(e => SameKind(e, device.Kind)).Select(e => (e.StableKey, e.LastKnownId)).ToList();
        return ofKind.Count > 0 && ExclusionRules.IsExcluded(ofKind, device.Id, device.StableKey, Connected(devices, device.Kind));
    }

    /// <summary>Exclusions for devices that are not in the list right now (e.g. unplugged). They are kept
    /// and shown, so the user can still remove them.</summary>
    public static List<DeviceExclusion> Orphans(IEnumerable<DeviceExclusion> exclusions, IReadOnlyCollection<DeviceLockInfo> devices) =>
        exclusions.Where(e => !devices.Any(d => Matches(e, d, devices))).ToList();

    /// <summary>The new exclusion list: one entry per excluded device, plus the orphans the user kept.</summary>
    public static List<DeviceExclusion> Build(IEnumerable<DeviceLockInfo> excludedDevices, IEnumerable<DeviceExclusion> keptOrphans)
    {
        var result = new List<DeviceExclusion>();
        foreach (DeviceLockInfo device in excludedDevices)
        {
            result.Add(new DeviceExclusion { StableKey = device.StableKey, LastKnownId = device.Id, DisplayName = device.Name });
        }

        result.AddRange(keptOrphans);

        // One entry per device (twins share a stable key but not an ID, so they stay separate).
        return result
            .GroupBy(e => e.LastKnownId ?? e.StableKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool SameKind(DeviceExclusion exclusion, DeviceKind kind) => KindOf(exclusion) is not DeviceKind known || known == kind;

    private static List<(string Id, string StableKey)> Connected(IReadOnlyCollection<DeviceLockInfo> devices, DeviceKind kind) =>
        devices.Where(d => d.Kind == kind && d.Protection != DeviceProtection.NotPresent).Select(d => (d.Id, d.StableKey)).ToList();
}
