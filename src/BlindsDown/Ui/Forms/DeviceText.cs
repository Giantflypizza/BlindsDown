using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;

namespace BlindsDown.Ui.Forms;

/// <summary>Plain-language descriptions of devices for the Settings "Devices" tab.</summary>
internal static class DeviceText
{
    public const string Empty = "–";

    /// <summary>What the lock does (or, when unlocked, would do) for one device. A failed lock shows its reason.</summary>
    public static string Protection(DeviceLockInfo device, bool locked) =>
        !locked && device.Protection == DeviceProtection.None && string.IsNullOrWhiteSpace(device.Problem)
            ? (device.HasHardwareMute ? "Can mute in hardware" : "Software mute only (exclusive-mode apps can bypass)")
            : Protection(device.Protection, device.Problem);

    /// <summary>What the lock currently does for one device. A failed lock shows its reason.</summary>
    public static string Protection(DeviceProtection protection, string? problem = null) => protection switch
    {
        DeviceProtection.HardwareMute => "Muted in hardware",
        DeviceProtection.SoftwareMute => "Muted in software (exclusive-mode apps can bypass)",
        DeviceProtection.DisabledByUser => "Disabled in Windows – left alone",
        DeviceProtection.NotPresent => "Not connected – locked when it appears",
        DeviceProtection.Excluded => "Excluded",
        _ when !string.IsNullOrWhiteSpace(problem) => "Not locked: " + problem,
        _ => Empty,
    };

    /// <summary>"Virtual" devices are software (Sonar, VB-Cable, ...): locking the physical mic behind them already silences them.</summary>
    public static string Type(bool isVirtual) => isVirtual ? "Virtual" : "Physical";
}

/// <summary>
/// Matching and rebuilding <see cref="AppSettings.ExcludedDevices"/> with the same rule the lock uses
/// (<see cref="ExclusionRules"/>): an exclusion belongs to the exact device it was made for, and follows
/// it to a new USB port only when no identical twin is connected.
/// </summary>
internal static class DeviceExclusions
{
    public static bool Matches(DeviceExclusion exclusion, DeviceLockInfo device, IReadOnlyCollection<DeviceLockInfo> devices) =>
        ExclusionRules.IsExcluded([(exclusion.StableKey, exclusion.LastKnownId)], device.Id, device.StableKey, Connected(devices));

    public static bool IsExcluded(IEnumerable<DeviceExclusion> exclusions, DeviceLockInfo device, IReadOnlyCollection<DeviceLockInfo> devices) =>
        ExclusionRules.IsExcluded(exclusions.Select(e => (e.StableKey, e.LastKnownId)), device.Id, device.StableKey, Connected(devices));

    /// <summary>Exclusions for devices that are not in the list right now (e.g. unplugged). They are kept
    /// and shown, so the user can still remove them.</summary>
    public static List<DeviceExclusion> Orphans(IEnumerable<DeviceExclusion> exclusions, IReadOnlyCollection<DeviceLockInfo> devices) =>
        exclusions.Where(e => !devices.Any(d => Matches(e, d, devices))).ToList();

    private static List<(string Id, string StableKey)> Connected(IReadOnlyCollection<DeviceLockInfo> devices) =>
        devices.Where(d => d.Protection != DeviceProtection.NotPresent).Select(d => (d.Id, d.StableKey)).ToList();

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
}
