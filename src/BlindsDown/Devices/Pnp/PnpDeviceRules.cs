using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Interop.Pnp;

namespace BlindsDown.Devices.Pnp;

/// <summary>
/// The pure rules of the Plug and Play layer: which devices count as cameras and as microphone-only hardware,
/// how cameras are classified, and how they are named. No Windows calls, so every rule is unit-tested.
/// </summary>
internal static class PnpDeviceRules
{
    /// <summary>
    /// Plausible PnP instance ID: "ENUMERATOR\device\instance", shorter than MAX_DEVICE_ID_LEN, no surrounding
    /// spaces or nulls. Checked before any ID reaches CfgMgr (an empty ID would mean "the root of the device tree").
    /// </summary>
    public static bool IsValidInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId)
        && instanceId.Length < CfgMgrNative.MaxDeviceIdLength
        && instanceId.Length == instanceId.Trim().Length
        && !instanceId.Contains('\0', StringComparison.Ordinal)
        && instanceId.IndexOf('\\', StringComparison.Ordinal) > 0;

    /// <summary>Bluetooth hardware (BTHENUM, BTHHFENUM, BTHLEDEVICE...). Its "hardware" is a profile of a wireless
    /// headset that also plays audio, and disabling it can drop the whole connection.</summary>
    public static bool IsBluetooth(string? instanceId) =>
        DeviceIds.Enumerator(instanceId) is { } enumerator && enumerator.StartsWith("BTH", StringComparison.Ordinal);

    /// <summary>One function of a composite USB device ("USB\VID_x&amp;PID_y&amp;MI_02\..."), e.g. a webcam's microphone.</summary>
    public static bool IsUsbFunction(string? instanceId)
    {
        if (instanceId is null || DeviceIds.Enumerator(instanceId) != "USB")
        {
            return false;
        }

        // Only the middle part (the hardware ID) says "MI_"; the last part is an opaque instance number.
        string[] parts = instanceId.Split('\\');
        return parts.Length >= 2 && parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Identifies one capture filter across interface classes. An interface path is
    /// "\\?\USB#VID_1234&amp;PID_5678&amp;MI_00#7&amp;1a2b3c4d&amp;0&amp;0000#{class guid}\reference"; the same filter
    /// registered in KSCATEGORY_VIDEO_CAMERA and KSCATEGORY_SENSOR_CAMERA differs only in the class GUID, so the
    /// key is the path without it (upper-cased: paths are case-insensitive).
    /// </summary>
    public static string FilterKey(string interfacePath)
    {
        string path = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? interfacePath[4..] : interfacePath;
        int slash = path.IndexOf('\\', StringComparison.Ordinal);
        string name = slash < 0 ? path : path[..slash];
        string reference = slash < 0 ? string.Empty : path[slash..];
        int classStart = name.LastIndexOf("#{", StringComparison.Ordinal);
        if (classStart >= 0 && name.EndsWith('}'))
        {
            name = name[..classStart];
        }

        return (name + reference).ToUpperInvariant();
    }

    /// <summary>
    /// Classifies one camera device from the filters it registers. A filter in KSCATEGORY_VIDEO_CAMERA is colour
    /// (normal apps can use it); a filter only in KSCATEGORY_SENSOR_CAMERA is infrared or depth (Windows Hello).
    /// Software devices are virtual whatever they register. Unsure means colour: it is locked by default, and a
    /// wrong guess only costs Windows Hello during the lock, never privacy.
    /// </summary>
    public static CameraType ClassifyCamera(string instanceId, IReadOnlySet<string> colorFilters, IReadOnlySet<string> sensorFilters)
    {
        if (DeviceIds.IsVirtualHardware(instanceId))
        {
            return CameraType.Virtual;
        }

        bool hasColor = colorFilters.Count > 0;
        bool hasInfrared = sensorFilters.Any(filter => !colorFilters.Contains(filter));
        return (hasColor, hasInfrared) switch
        {
            (true, true) => CameraType.ColorAndInfrared,
            (false, true) => CameraType.Infrared,
            _ => CameraType.Color,
        };
    }

    /// <summary>
    /// The name to show: FriendlyName, else the name the hardware reports, else the driver's description, else
    /// <paramref name="fallback"/>. An unresolved "@driver.inf,%key%;Text" description gives its "Text".
    /// </summary>
    public static string ChooseName(string? friendlyName, string? busReportedName, string? description, string fallback)
    {
        foreach (string? candidate in new[] { friendlyName, busReportedName, description })
        {
            string? text = candidate?.Trim();
            if (text is not null && text.StartsWith('@'))
            {
                // A resource reference without a default text ("@usbvideo.inf,%key%") has nothing readable.
                int semicolon = text.LastIndexOf(';');
                text = semicolon > 0 ? text[(semicolon + 1)..].Trim() : null;
            }

            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Hardware behind microphones that has no speakers or headphones, so disabling it can only take microphones
    /// away. Grouped by hardware instance ID over endpoints in EVERY state (a headset's unplugged headphones still
    /// count), a candidate has capture endpoints and no render endpoint, and is neither software (ROOT, SWD, SW)
    /// nor Bluetooth. A USB function is also refused when another function of the same USB device
    /// (<paramref name="parentOf"/>) has render endpoints: some headsets put the microphone on a function of its
    /// own, and BlindsDown never touches hardware that also plays sound.
    /// </summary>
    public static List<string> SelectMicOnlyHardware(IEnumerable<AudioEndpointHardware> endpoints, Func<string, string?> parentOf)
    {
        var capture = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var render = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (AudioEndpointHardware endpoint in endpoints)
        {
            if (!IsValidInstanceId(endpoint.HardwareInstanceId))
            {
                continue;
            }

            if (!endpoint.IsCapture)
            {
                render.Add(endpoint.HardwareInstanceId);
            }
            else if (capture.Add(endpoint.HardwareInstanceId))
            {
                candidates.Add(endpoint.HardwareInstanceId);
            }
        }

        candidates.RemoveAll(id => render.Contains(id) || DeviceIds.IsVirtualHardware(id) || IsBluetooth(id));
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var parentsWithSpeakers = new HashSet<string>(
            render.Where(IsUsbFunction).Select(parentOf).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        candidates.RemoveAll(id => IsUsbFunction(id) && parentOf(id) is { } parent && parentsWithSpeakers.Contains(parent));
        return candidates;
    }
}
