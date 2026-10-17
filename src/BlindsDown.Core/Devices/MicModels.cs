using System.Text.Json.Serialization;

namespace BlindsDown.Core.Devices;

/// <summary>
/// Audio endpoint states. The values are the Windows DEVICE_STATE_* constants.
/// </summary>
[Flags]
public enum EndpointState
{
    None = 0,
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
}

/// <summary>A microphone (audio capture endpoint) as Windows reports it.</summary>
public sealed record MicEndpoint
{
    /// <summary>The opaque Windows endpoint ID, e.g. "{0.0.1.00000000}.{guid}". Compare, never parse.</summary>
    public required string Id { get; init; }

    /// <summary>Full display name, e.g. "Microphone (Arctis Nova Pro Wireless)".</summary>
    public required string Name { get; init; }

    /// <summary>The endpoint's own description without the adapter name, e.g. "Microphone".</summary>
    public string? EndpointDescription { get; init; }

    public required EndpointState State { get; init; }

    /// <summary>Current mute state, or null when it cannot be read (device not present).</summary>
    public bool? Muted { get; init; }

    /// <summary>
    /// True when the device mutes in hardware. Without hardware mute, Windows mutes in software,
    /// and exclusive-mode apps can bypass that.
    /// </summary>
    public bool HasHardwareMute { get; init; }

    /// <summary>PnP instance ID of the hardware behind the endpoint, e.g. "USB\VID_1038&amp;PID_12E0&amp;MI_00\9&amp;1601...".</summary>
    public string? HardwareInstanceId { get; init; }

    /// <summary>True when the hardware is a software (virtual) device such as SteelSeries Sonar or VB-Cable.</summary>
    [JsonIgnore]
    public bool IsVirtual => DeviceIds.IsVirtualHardware(HardwareInstanceId);

    /// <summary>A stable identity for exclusions that survives USB port changes.</summary>
    [JsonIgnore]
    public string StableKey => DeviceIds.StableMicKey(HardwareInstanceId, Name);

    [JsonIgnore]
    public bool IsUsable => State is EndpointState.Active or EndpointState.Unplugged;
}

/// <summary>The default microphone for each of the three Windows roles.</summary>
public sealed record DefaultMics(string? Console, string? Multimedia, string? Communications)
{
    public static DefaultMics None { get; } = new(null, null, null);

    public bool Contains(string endpointId) =>
        string.Equals(Console, endpointId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Multimedia, endpointId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Communications, endpointId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything we know about the microphones at one moment.</summary>
public sealed record AudioSnapshot(IReadOnlyList<MicEndpoint> Mics, DefaultMics Defaults, DateTimeOffset TakenAt)
{
    public MicEndpoint? Find(string endpointId) =>
        Mics.FirstOrDefault(m => string.Equals(m.Id, endpointId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Helpers for Windows device identifiers.</summary>
public static class DeviceIds
{
    /// <summary>Bus enumerators that only ever host software (virtual) devices.</summary>
    private static readonly string[] VirtualEnumerators = ["ROOT", "SWD", "SW"];

    /// <summary>
    /// The first segment of a PnP instance ID is the enumerator: USB, HDAUDIO, BTHENUM, ROOT, SWD...
    /// </summary>
    public static string? Enumerator(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        int slash = instanceId.IndexOf('\\', StringComparison.Ordinal);
        return (slash < 0 ? instanceId : instanceId[..slash]).ToUpperInvariant();
    }

    public static bool IsVirtualHardware(string? hardwareInstanceId)
    {
        string? enumerator = Enumerator(hardwareInstanceId);
        return enumerator is not null && VirtualEnumerators.Contains(enumerator, StringComparer.Ordinal);
    }

    /// <summary>
    /// Instance IDs look like "USB\VID_1038&amp;PID_12E0&amp;MI_00\9&amp;16010608&amp;0&amp;0000". The last part
    /// changes when the same device is plugged into another port, so the stable part is everything before it.
    /// </summary>
    public static string HardwareKey(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return string.Empty;
        }

        int last = instanceId.LastIndexOf('\\');
        string key = last > 0 ? instanceId[..last] : instanceId;
        return key.ToUpperInvariant();
    }

    /// <summary>
    /// Readable name for a virtual audio device in "Discord (via SteelSeries Sonar)": drops generic
    /// suffixes such as " Virtual Audio Device" that driver names usually carry.
    /// </summary>
    public static string? ShortVirtualDeviceName(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return null;
        }

        string name = adapterName.Trim();
        foreach (string suffix in new[] { " Virtual Audio Device", " Virtual Audio Driver", " Virtual Audio", " Virtual Device", " Audio Device" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length].TrimEnd(' ', '-', '(');
            }
        }

        return name;
    }

    /// <summary>
    /// Identity of a microphone that survives a USB port change: the hardware model part of the instance
    /// ID plus the full name. The full name matters because some buses use the same generic ID for every
    /// device (all Bluetooth hands-free headsets are "BTHHFENUM\BTHHFPAUDIO"); the model is only in the
    /// name. Windows' "2- " numbering of duplicate devices is removed, since it changes with plug order.
    /// Identical twins (same model, same name) still share a key: <see cref="ExclusionRules"/> handles that.
    /// </summary>
    public static string StableMicKey(string? hardwareInstanceId, string name) =>
        $"mic|{HardwareKey(hardwareInstanceId)}|{DuplicateNumber.Replace(name.Trim(), "(").ToUpperInvariant()}";

    private static readonly System.Text.RegularExpressions.Regex DuplicateNumber =
        new(@"\(\d+- ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>
/// Decides whether a device is excluded from the lock. An exclusion belongs to the exact device it was
/// made for (its Windows ID). Only when that device is gone (e.g. plugged into another USB port, which
/// gives it a new ID) does the exclusion follow it by <see cref="DeviceIds.StableMicKey"/>, and only if
/// exactly one connected device has that identity, so excluding one of two identical mics never
/// excludes the other.
/// </summary>
public static class ExclusionRules
{
    /// <param name="exclusions">(StableKey, LastKnownId) of every exclusion.</param>
    /// <param name="id">The device being checked.</param>
    /// <param name="stableKey">Its stable key.</param>
    /// <param name="connected">(Id, StableKey) of every device that is connected right now (not "not present").</param>
    public static bool IsExcluded(
        IEnumerable<(string StableKey, string? LastKnownId)> exclusions,
        string id,
        string stableKey,
        IReadOnlyCollection<(string Id, string StableKey)> connected)
    {
        var list = exclusions.ToList();
        if (list.Exists(e => e.LastKnownId is not null && Same(e.LastKnownId, id)))
        {
            return true;
        }

        foreach ((string key, string? lastKnownId) in list.Where(e => Same(e.StableKey, stableKey)))
        {
            if (lastKnownId is not null && connected.Any(c => Same(c.Id, lastKnownId)))
            {
                continue; // The device this exclusion was made for is still here: it isn't this one.
            }

            if (connected.Count(c => Same(c.StableKey, key)) <= 1)
            {
                return true; // The excluded device moved (new ID), and nothing else could be meant.
            }
        }

        return false;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
