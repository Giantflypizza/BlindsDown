using System.Runtime.InteropServices;

namespace BlindsDown.Interop.Media;

/// <summary>DEVPROPKEY: a property set GUID plus a property number (same layout as PROPERTYKEY).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct CameraPropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;

    /// <summary>DEVPKEY_Device_InstanceId: the PnP instance ID of the device behind an interface.</summary>
    public static CameraPropertyKey InstanceId => new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    /// <summary>DEVPKEY_NAME: the name Device Manager shows (the friendly name, else the device description).</summary>
    public static CameraPropertyKey Name => new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
}

/// <summary>SERVICE_STATUS_PROCESS (QueryServiceStatusEx with SC_STATUS_PROCESS_INFO).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CameraServiceStatus
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
    public uint ProcessId;
    public uint ServiceFlags;
}

/// <summary>
/// Read-only Windows lookups for the camera activity monitor: the device behind a camera's symbolic link (CfgMgr32)
/// and the process of the Windows Camera Frame Server (the service control manager). Nothing here changes device or
/// system state. Every import is pinned to System32: cfgmgr32.dll is NOT a KnownDLL, and even for a KnownDLL such as
/// advapi32.dll the runtime would first look next to the exe, where a planted copy (say, in a Downloads folder)
/// must never be loaded.
/// </summary>
internal static unsafe partial class CameraLookupNative
{
    public const int CrSuccess = 0;

    /// <summary>CR_BUFFER_SMALL: the size needed was written to the size argument.</summary>
    public const int CrBufferSmall = 0x1A;

    /// <summary>DEVPROP_TYPE_STRING: a null-terminated UTF-16 string.</summary>
    public const uint DevPropTypeString = 0x12;

    /// <summary>CM_LOCATE_DEVNODE_NORMAL: only devices that are present.</summary>
    public const uint CmLocateDevNodeNormal = 0;

    /// <summary>SC_MANAGER_CONNECT: enough to open a service by name.</summary>
    public const uint ScManagerConnect = 0x0001;

    /// <summary>SERVICE_QUERY_STATUS: granted to every signed-in user for Windows services.</summary>
    public const uint ServiceQueryStatus = 0x0004;

    /// <summary>SC_STATUS_PROCESS_INFO.</summary>
    public const int ScStatusProcessInfo = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CM_Get_Device_Interface_PropertyW(
        char* deviceInterface, CameraPropertyKey* propertyKey, uint* propertyType, byte* buffer, uint* bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CM_Locate_DevNodeW(uint* devInst, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CM_Get_DevNode_PropertyW(
        uint devInst, CameraPropertyKey* propertyKey, uint* propertyType, byte* buffer, uint* bufferSize, uint flags);

    /// <summary>Always called with a null machine name: the local service control manager only.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint OpenSCManagerW(char* machineName, char* databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint OpenServiceW(nint manager, char* serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int QueryServiceStatusEx(nint service, int infoLevel, CameraServiceStatus* buffer, uint bufferSize, uint* bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CloseServiceHandle(nint handle);
}
