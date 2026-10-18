using System.Runtime.InteropServices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Interop.Media;

// Media Foundation's sensor activity monitor (mfidl.h, Windows 10 1703 and later): which processes are using which
// camera, as the Windows Camera Frame Server sees it. No admin rights needed, and unlike the usage registry it names
// the process and the camera.
//
// IIDs and vtable orders are those of the Windows SDK's mfidl.h (cross-checked against TerraFX.Interop.Windows, which
// is generated from the SDK 10.0.28000 headers, and against the IIDs compiled into this PC's mfsensorgroup.dll).
// Classic COM interop maps methods to vtable slots purely by declaration order, so each interface lists every method
// in header order; a wrong order would call the wrong native method. Methods that return objects use raw pointers
// ("out nint"): the caller wraps them in ComRef (deterministic release) and reads nothing unless the HRESULT
// succeeded. Strings are copied into caller-owned buffers.

/// <summary>
/// IMFSensorActivityMonitor: created by MFCreateSensorActivityMonitor; reports flow between Start and Stop. Shut it
/// down (<see cref="IMFShutdown"/>) after Stop and before releasing it.
/// </summary>
[ComImport]
[Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityMonitor
{
    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();
}

/// <summary>
/// IMFSensorActivitiesReportCallback, implemented by BlindsDown. Media Foundation calls it on its own worker threads;
/// the report is borrowed and valid only during the call.
/// </summary>
[ComImport]
[Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReportCallback
{
    /// <summary><paramref name="activitiesReport"/> is an IMFSensorActivitiesReport the caller keeps owning.</summary>
    [PreserveSig]
    int OnActivitiesReport(nint activitiesReport);
}

/// <summary>IMFSensorActivitiesReport: one IMFSensorActivityReport per camera with activity.</summary>
[ComImport]
[Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivitiesReport
{
    [PreserveSig]
    int GetCount(out uint count);

    /// <summary>Returns an IMFSensorActivityReport.</summary>
    [PreserveSig]
    int GetActivityReport(uint index, out nint report);

    /// <summary>Returns an IMFSensorActivityReport, or MF_E_NOT_FOUND.</summary>
    [PreserveSig]
    int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string symbolicName, out nint report);
}

/// <summary>IMFSensorActivityReport: one camera (by its device interface symbolic link) and the processes using it.</summary>
[ComImport]
[Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorActivityReport
{
    /// <summary>Copies the camera's name into <paramref name="buffer"/> (<paramref name="capacity"/> WCHARs).</summary>
    [PreserveSig]
    int GetFriendlyName(nint buffer, uint capacity, out uint written);

    /// <summary>Copies the camera's device interface path, e.g. "\\?\USB#VID_046D&amp;PID_085C&amp;MI_00#...#{e5323777-...}\GLOBAL".</summary>
    [PreserveSig]
    int GetSymbolicLink(nint buffer, uint capacity, out uint written);

    [PreserveSig]
    int GetProcessCount(out uint count);

    /// <summary>Returns an IMFSensorProcessActivity.</summary>
    [PreserveSig]
    int GetProcessActivity(uint index, out nint activity);
}

/// <summary>IMFSensorProcessActivity: one process that has the camera open.</summary>
[ComImport]
[Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSensorProcessActivity
{
    [PreserveSig]
    int GetProcessId(out uint processId);

    /// <summary><paramref name="streaming"/> is a Win32 BOOL: non-zero while the process receives frames, not just has the camera open.</summary>
    [PreserveSig]
    int GetStreamingState(out int streaming);

    [PreserveSig]
    int GetStreamingMode(out SensorDeviceMode mode);

    /// <summary><paramref name="fileTime"/> is a FILETIME (UTC).</summary>
    [PreserveSig]
    int GetReportTime(out long fileTime);
}

/// <summary>
/// IMFShutdown (mfidl.h), which the sensor activity monitor also exposes (its IID is in mfsensorgroup.dll). Its
/// documented contract: whoever creates an object that has it shuts the object down before releasing it, because a
/// release alone may leave the object's resources held. Microsoft's sample shuts the monitor down after Stop.
/// </summary>
[ComImport]
[Guid("97EC2EA4-0E42-4937-97AC-9D6D328824E1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFShutdown
{
    [PreserveSig]
    int Shutdown();

    /// <summary>GetShutdownStatus(MFSHUTDOWN_STATUS* status).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetShutdownStatus(nint status);
}

/// <summary>MFSensorDeviceMode: whether the process controls the camera or shares another app's stream.</summary>
internal enum SensorDeviceMode
{
    Controller = 0,
    Shared = 1,
}
