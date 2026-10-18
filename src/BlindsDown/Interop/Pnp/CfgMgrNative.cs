using System.Runtime.InteropServices;

namespace BlindsDown.Interop.Pnp;

/// <summary>
/// Raw CfgMgr32 declarations for the Plug and Play layer. Every function returns a CONFIGRET (CR_*) instead of
/// setting the last error. Only <see cref="CM_Disable_DevNode"/> and <see cref="CM_Enable_DevNode"/> change
/// anything; everything else reads or (un)registers notifications.
///
/// cfgmgr32.dll is NOT a KnownDLL, so every import is pinned to System32: a planted cfgmgr32.dll next to the exe
/// (say, in a Downloads folder) must never be loaded, least of all by the LocalSystem helper.
/// </summary>
internal static unsafe partial class CfgMgrNative
{
    private const string Library = "cfgmgr32.dll";

    // CONFIGRET values (cfgmgr32.h). Only the ones BlindsDown tells apart; anything else is shown as a number.
    public const uint CrSuccess = 0x00;
    public const uint CrOutOfMemory = 0x02;
    public const uint CrInvalidPointer = 0x03;
    public const uint CrInvalidFlag = 0x04;
    public const uint CrInvalidDevNode = 0x05;
    public const uint CrNoSuchDevNode = 0x0D;
    public const uint CrFailure = 0x13;
    public const uint CrRemoveVetoed = 0x17;
    public const uint CrBufferSmall = 0x1A;
    public const uint CrInvalidDeviceId = 0x1E;
    public const uint CrInvalidData = 0x1F;
    public const uint CrNeedRestart = 0x22;
    public const uint CrDeviceNotThere = 0x24;
    public const uint CrNoSuchValue = 0x25;
    public const uint CrNotDisableable = 0x28;
    public const uint CrAccessDenied = 0x33;
    public const uint CrCallNotImplemented = 0x34;
    public const uint CrNoSuchDeviceInterface = 0x37;

    /// <summary>CM_LOCATE_DEVNODE_PHANTOM: also find devices that are installed but not connected right now.
    /// Without it, CM_Locate_DevNodeW fails with CR_NO_SUCH_DEVNODE for an unplugged webcam.</summary>
    public const uint CmLocateDevNodeNormal = 0x0;
    public const uint CmLocateDevNodePhantom = 0x1;

    /// <summary>CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES: interfaces of disabled and unplugged devices too.
    /// The default (PRESENT) lists only enabled interfaces, which would hide a camera BlindsDown disabled.</summary>
    public const uint CmGetDeviceInterfaceListAllDevices = 0x1;

    /// <summary>CM_DISABLE_UI_NOT_OK: never show "restart your computer" UI (the helper runs in session 0 anyway).</summary>
    public const uint CmDisableUiNotOk = 0x4;

    /// <summary>CM_DISABLE_PERSIST: survive a reboot. BlindsDown NEVER passes it: a reboot must always bring every
    /// device back, even if BlindsDown is gone. Declared only so tests can check that it is never set.</summary>
    public const uint CmDisablePersist = 0x8;

    // DN_* status bits (cfg.h).
    public const uint DnStarted = 0x00000008;

    /// <summary>DN_NEED_RESTART (also called DN_LIAR): a change only takes effect after a restart.</summary>
    public const uint DnNeedRestart = 0x00000100;
    public const uint DnHasProblem = 0x00000400;

    // CM_PROB_* problem codes (cfg.h).
    public const uint CmProbNeedRestart = 14;
    public const uint CmProbDisabled = 22;

    /// <summary>CONFIGFLAG_DISABLED in DEVPKEY_Device_ConfigFlags: disabled persistently (Device Manager,
    /// Settings, policy). The only "disabled" state Windows keeps for a device that isn't connected.</summary>
    public const uint ConfigFlagDisabled = 0x00000001;

    // DEVPROPTYPE values (devpropdef.h).
    public const uint DevPropTypeUInt32 = 0x00000007;
    public const uint DevPropTypeGuid = 0x0000000D;
    public const uint DevPropTypeBoolean = 0x00000011;
    public const uint DevPropTypeString = 0x00000012;

    // CM_NOTIFY_FILTER (cfgmgr32.h).
    public const uint CmNotifyFilterFlagAllDeviceInstances = 0x2;
    public const uint CmNotifyFilterTypeDeviceInterface = 0;
    public const uint CmNotifyFilterTypeDeviceInstance = 2;

    /// <summary>MAX_DEVICE_ID_LEN: instance IDs are shorter than this (without the terminating null).</summary>
    public const int MaxDeviceIdLength = 200;

    public const uint ErrorSuccess = 0;

    /// <summary>Length in characters (with the final double null) of the interface list. Can grow before the list is read.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_Device_Interface_List_SizeW(uint* length, Guid* interfaceClassGuid, char* deviceId, uint flags);

    /// <summary>Fills a multi-sz list of interface paths. CR_BUFFER_SMALL when it grew since the size query.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Get_Device_Interface_ListW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_Device_Interface_ListW(Guid* interfaceClassGuid, char* deviceId, char* buffer, uint bufferLength, uint flags);

    /// <summary>CR_BUFFER_SMALL with the needed size in <paramref name="bufferSize"/> when the buffer is too small.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_Device_Interface_PropertyW(char* deviceInterface, DevPropKey* propertyKey, uint* propertyType, byte* buffer, uint* bufferSize, uint flags);

    /// <summary>A null or EMPTY device ID locates the root of the device tree, so callers must never pass one.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Locate_DevNodeW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Locate_DevNodeW(uint* devInst, char* deviceId, uint flags);

    /// <summary>CR_NO_SUCH_DEVINST for a device that isn't connected (a phantom devnode has no live status).</summary>
    [LibraryImport(Library, EntryPoint = "CM_Get_DevNode_Status")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_DevNode_Status(uint* status, uint* problemNumber, uint devInst, uint flags);

    /// <summary>CR_NO_SUCH_VALUE when the device has no such property; CR_BUFFER_SMALL with the needed size.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Get_DevNode_PropertyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Get_DevNode_PropertyW(uint devInst, DevPropKey* propertyKey, uint* propertyType, byte* buffer, uint* bufferSize, uint flags);

    /// <summary>Needs administrator rights. Query-removes the device first, so an app holding it open can veto.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Disable_DevNode")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Disable_DevNode(uint devInst, uint flags);

    /// <summary>Needs administrator rights. No flags are defined: pass 0.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Enable_DevNode")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Enable_DevNode(uint devInst, uint flags);

    /// <summary>
    /// The callback runs on a Windows thread pool thread. It must not call CM_Unregister_Notification (deadlock)
    /// and should not call other CfgMgr functions either. The callback is a static
    /// [UnmanagedCallersOnly] method, so there is no delegate the garbage collector could free.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "CM_Register_Notification")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Register_Notification(
        CmNotifyFilter* filter,
        nint context,
        delegate* unmanaged[Stdcall]<nint, nint, uint, nint, uint, uint> callback,
        nint* notifyContext);

    /// <summary>Waits for callbacks that are running to finish, so it must never be called from a callback.</summary>
    [LibraryImport(Library, EntryPoint = "CM_Unregister_Notification")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint CM_Unregister_Notification(nint notifyContext);

    /// <summary>DEVPROPKEY: a property set GUID plus a property ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>
    /// CM_NOTIFY_FILTER. The union after the four DWORDs is as large as its biggest member,
    /// WCHAR InstanceId[MAX_DEVICE_ID_LEN], so the structure is 16 + 400 = 416 bytes, and cbSize must say exactly
    /// that (otherwise CR_INVALID_STRUCTURE_SIZE). Only the interface-class member of the union is used.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = StructSize)]
    public struct CmNotifyFilter
    {
        public const int StructSize = 16 + (MaxDeviceIdLength * sizeof(char));

        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public uint Flags;

        [FieldOffset(8)]
        public uint FilterType;

        [FieldOffset(12)]
        public uint Reserved;

        [FieldOffset(16)]
        public Guid ClassGuid;
    }
}
