using System.Runtime.InteropServices;

// Deliberately not "BlindsDown.Interop.System": a namespace called System would shadow the real System
// namespace for every file under BlindsDown.Interop.
namespace BlindsDown.Interop.SystemApis;

/// <summary>
/// Raw Win32 declarations for the system services (process identity, the usage registry watch and
/// service names). Every function here only reads: nothing changes system or device state.
/// All DLLs are KnownDLLs (always loaded from System32), so the DLL search order cannot be hijacked.
/// </summary>
internal static unsafe partial class SystemNative
{
    /// <summary>Enough to read a process's image name, times and package identity. Unlike
    /// PROCESS_QUERY_INFORMATION it is granted for most processes of the same user, including elevated ones.</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorMoreData = 234;

    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    /// <summary>SYSTEM_INFORMATION_CLASS.SystemProcessInformation: every process with its creation time.</summary>
    public const int SystemProcessInformationClass = 5;

    /// <summary>SYSTEM_INFORMATION_CLASS.SystemProcessIdInformation: the image path of any process by ID,
    /// without opening it. This is the only way to name processes (SYSTEM services) we may not open.</summary>
    public const int SystemProcessIdInformationClass = 88;

    public const uint RegNotifyChangeName = 0x00000001;
    public const uint RegNotifyChangeLastSet = 0x00000004;

    /// <summary>Without this flag the notification is cancelled when the thread that registered it exits,
    /// and our re-arming runs on short-lived thread-pool threads.</summary>
    public const uint RegNotifyThreadAgnostic = 0x10000000;

    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerEnumerateService = 0x0004;
    public const int ScEnumProcessInfo = 0;
    public const uint ServiceWin32 = 0x00000030;
    public const uint ServiceActive = 0x00000001;

    /// <summary>ASFW_ANY: any process may take the foreground once (used before handing "show" to the running instance).</summary>
    internal const int AsfwAny = -1;

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    public static partial int QueryFullProcessImageNameW(nint process, uint flags, char* exeName, uint* size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    public static partial int GetProcessTimes(nint process, long* creationTime, long* exitTime, long* kernelTime, long* userTime);

    /// <summary>Returns a Win32 error code (no last-error). There is no A/W pair: the export is Unicode only.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackageFamilyName")]
    public static partial int GetPackageFamilyName(nint process, uint* length, char* packageFamilyName);

    /// <summary>Returns a Win32 error code (no last-error). Unicode-only export.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackageFullName")]
    public static partial int GetPackageFullName(nint process, uint* length, char* packageFullName);

    /// <summary>Returns a Win32 error code (no last-error). Unicode-only export.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackagePathByFullName")]
    public static partial int GetPackagePathByFullName(char* packageFullName, uint* pathLength, char* path);

    /// <summary>Returns a Win32 error code (no last-error). Unicode-only export.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackagesByPackageFamily")]
    public static partial int GetPackagesByPackageFamily(char* packageFamilyName, uint* count, char** packageFullNames, uint* bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true)]
    public static partial uint QueryDosDeviceW(char* deviceName, char* targetPath, uint maxChars);

    /// <summary>Returns an NTSTATUS (negative = failure).</summary>
    [LibraryImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint length, uint* returnLength);

    /// <summary>Returns a Win32 error code (no last-error). Raw handles keep the signature blittable; use
    /// <see cref="SystemQueries.NotifyKeyChange"/>, which holds a reference on both SafeHandles for the call.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegNotifyChangeKeyValue")]
    public static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint notifyFilter, nint eventHandle, int asynchronous);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    public static partial nint OpenSCManagerW(char* machineName, char* databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    public static partial int EnumServicesStatusExW(
        nint serviceManager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        byte* services,
        uint bufferSize,
        uint* bytesNeeded,
        uint* servicesReturned,
        uint* resumeHandle,
        char* groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    public static partial int CloseServiceHandle(nint serviceHandle);

    /// <summary>Resolves "@{PackageFullName?ms-resource://...}" strings from the package's resources.pri.
    /// Returns an HRESULT. Unicode-only export.</summary>
    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString")]
    public static partial int SHLoadIndirectString(char* source, char* outBuffer, uint outBufferChars, nint reserved);

    /// <summary>UNICODE_STRING. Length and MaximumLength are in bytes, and Buffer is not null-terminated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    /// <summary>SYSTEM_PROCESS_ID_INFORMATION. The caller supplies ImageName.Buffer; on
    /// STATUS_INFO_LENGTH_MISMATCH the kernel writes the needed size to ImageName.MaximumLength.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessIdInformation
    {
        public nint ProcessId;
        public UnicodeString ImageName;
    }

    /// <summary>
    /// The leading, long-stable part of SYSTEM_PROCESS_INFORMATION (winternl.h hides it as Reserved1;
    /// System.Diagnostics.Process reads the same layout). Only the fields up to UniqueProcessId are declared:
    /// entries are walked by NextEntryOffset, never by sizeof.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
    }

    /// <summary>SERVICE_STATUS_PROCESS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
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

    /// <summary>ENUM_SERVICE_STATUS_PROCESSW. The strings point into the same buffer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public ServiceStatusProcess ServiceStatus;
    }
}
