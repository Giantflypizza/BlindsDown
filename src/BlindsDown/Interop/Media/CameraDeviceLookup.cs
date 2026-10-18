using System.Runtime.InteropServices;

namespace BlindsDown.Interop.Media;

/// <summary>
/// The camera device behind a symbolic link: its PnP instance ID (the same ID the helper service lists cameras by)
/// and its name. Either is null when Windows doesn't know it, e.g. the camera was unplugged a moment ago.
/// </summary>
internal readonly record struct CameraDeviceInfo(string? InstanceId, string? Name);

/// <summary>Read-only lookups for the camera activity monitor. Never throw; failures give null or an empty set.</summary>
internal static unsafe class CameraDeviceLookup
{
    /// <summary>Property values larger than this are not names or IDs; refuse them rather than allocate.</summary>
    private const uint MaxPropertyBytes = 64 * 1024;

    /// <summary>
    /// The Windows Camera Frame Server and its monitor: they run in a svchost and open cameras on behalf of apps, so
    /// they may appear in activity reports next to (or instead of) the app itself.
    /// </summary>
    private static readonly string[] FrameServerServices = ["FrameServer", "FrameServerMonitor"];

    /// <summary>The device behind a camera's device interface (its symbolic link).</summary>
    public static CameraDeviceInfo Resolve(string symbolicLink)
    {
        string? instanceId = InterfaceInstanceId(symbolicLink);
        return new CameraDeviceInfo(instanceId, instanceId is null ? null : DeviceName(instanceId));
    }

    /// <summary>The process IDs of the frame server services that are running right now (often none: they stop when idle).</summary>
    public static IReadOnlySet<int> FrameServerProcessIds()
    {
        var ids = new HashSet<int>();
        nint manager = CameraLookupNative.OpenSCManagerW(null, null, CameraLookupNative.ScManagerConnect);
        if (manager == 0)
        {
            return ids;
        }

        try
        {
            foreach (string name in FrameServerServices)
            {
                if (ServiceProcessId(manager, name) is { } id)
                {
                    ids.Add(id);
                }
            }
        }
        finally
        {
            _ = CameraLookupNative.CloseServiceHandle(manager);
        }

        return ids;
    }

    /// <summary>
    /// Turns a DEVPROP_TYPE_STRING value (UTF-16 bytes, normally ending with a null terminator) into a string. Returns
    /// null for another type or an empty value, and never reads past <paramref name="size"/> or the buffer.
    /// </summary>
    internal static string? DecodeString(uint type, ReadOnlySpan<byte> bytes, uint size)
    {
        if (type != CameraLookupNative.DevPropTypeString)
        {
            return null;
        }

        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(bytes[..(int)Math.Min(size, (uint)bytes.Length)]);
        int end = text.IndexOf('\0');
        string value = (end < 0 ? text : text[..end]).ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// DEVPKEY_Device_InstanceId of the interface, read with the usual two calls (ask for the size, then read). A
    /// symbolic link that names no interface (or one that is gone) gives null.
    /// </summary>
    private static string? InterfaceInstanceId(string symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink))
        {
            return null;
        }

        CameraPropertyKey key = CameraPropertyKey.InstanceId;
        uint type = 0;
        uint size = 0;
        fixed (char* path = symbolicLink)
        {
            int result = CameraLookupNative.CM_Get_Device_Interface_PropertyW(path, &key, &type, null, &size, 0);
            if (!IsReadableString(result, type, size))
            {
                return null;
            }

            byte[] bytes = new byte[size];
            fixed (byte* buffer = bytes)
            {
                result = CameraLookupNative.CM_Get_Device_Interface_PropertyW(path, &key, &type, buffer, &size, 0);
            }

            return result == CameraLookupNative.CrSuccess ? DecodeString(type, bytes, size) : null;
        }
    }

    /// <summary>DEVPKEY_NAME of a present device, the same two-call way.</summary>
    private static string? DeviceName(string instanceId)
    {
        uint devInst;
        fixed (char* id = instanceId)
        {
            if (CameraLookupNative.CM_Locate_DevNodeW(&devInst, id, CameraLookupNative.CmLocateDevNodeNormal) != CameraLookupNative.CrSuccess)
            {
                return null;
            }
        }

        CameraPropertyKey key = CameraPropertyKey.Name;
        uint type = 0;
        uint size = 0;
        int result = CameraLookupNative.CM_Get_DevNode_PropertyW(devInst, &key, &type, null, &size, 0);
        if (!IsReadableString(result, type, size))
        {
            return null;
        }

        byte[] bytes = new byte[size];
        fixed (byte* buffer = bytes)
        {
            result = CameraLookupNative.CM_Get_DevNode_PropertyW(devInst, &key, &type, buffer, &size, 0);
        }

        return result == CameraLookupNative.CrSuccess ? DecodeString(type, bytes, size) : null;
    }

    /// <summary>The size query found a string value small enough to be a name or an ID.</summary>
    private static bool IsReadableString(int result, uint type, uint size) =>
        result == CameraLookupNative.CrBufferSmall && type == CameraLookupNative.DevPropTypeString && size is > 0 and <= MaxPropertyBytes;

    private static int? ServiceProcessId(nint manager, string serviceName)
    {
        nint service;
        fixed (char* name = serviceName)
        {
            service = CameraLookupNative.OpenServiceW(manager, name, CameraLookupNative.ServiceQueryStatus);
        }

        if (service == 0)
        {
            return null;
        }

        try
        {
            CameraServiceStatus status;
            uint needed;
            int ok = CameraLookupNative.QueryServiceStatusEx(
                service, CameraLookupNative.ScStatusProcessInfo, &status, (uint)sizeof(CameraServiceStatus), &needed);
            return ok != 0 && status.ProcessId is > 0 and <= int.MaxValue ? (int)status.ProcessId : null;
        }
        finally
        {
            _ = CameraLookupNative.CloseServiceHandle(service);
        }
    }
}
