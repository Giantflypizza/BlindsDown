using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>
/// Raw Win32 declarations for the optional helper: its local named pipe, the service control manager, the
/// process token, security descriptors, the setup's mutex, delayed file deletion and waiting for a process. Starting a process
/// (ShellExecuteExW) is deliberately NOT here: only <see cref="Infrastructure.SafeShell"/> may do that.
/// All DLLs are KnownDLLs (always loaded from System32), so the DLL search order cannot be hijacked.
/// </summary>
internal static unsafe partial class HelperNative
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBrokenPipe = 109;
    public const int ErrorSemTimeout = 121;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorAlreadyExists = 183;
    public const int ErrorPipeBusy = 231;
    public const int ErrorNoData = 232;
    public const int ErrorPipeNotConnected = 233;
    public const int ErrorMoreData = 234;
    public const int ErrorIoPending = 997;
    public const int ErrorServiceAlreadyRunning = 1056;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceMarkedForDelete = 1072;
    public const int ErrorInvalidOwner = 1307;

    public const nint InvalidHandleValue = -1;

    public const uint ReadControl = 0x00020000;
    public const uint Synchronize = 0x00100000;
    public const uint MutexModifyState = 0x0001;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION: the pipe server may learn who the client is,
    /// but can't act as the client (impersonation level SecurityIdentification).</summary>
    public const uint SecuritySqosPresent = 0x00100000;
    public const uint SecurityIdentification = 0x00010000;

    public const uint PipeAccessDuplex = 0x00000003;

    /// <summary>Fails if any instance of the name already exists, so a program that grabbed the name first
    /// can't make the helper join ITS pipe.</summary>
    public const uint FileFlagFirstPipeInstance = 0x00080000;
    public const uint PipeTypeMessage = 0x00000004;
    public const uint PipeReadModeMessage = 0x00000002;
    public const uint PipeRejectRemoteClients = 0x00000008;

    /// <summary>SDDL_REVISION_1.</summary>
    public const uint SddlRevision1 = 1;

    /// <summary>SE_OBJECT_TYPE.SE_KERNEL_OBJECT: works on any kernel handle, including a pipe.</summary>
    public const int SeKernelObject = 6;
    public const uint OwnerSecurityInformation = 0x00000001;

    public const uint TokenQuery = 0x0008;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation.</summary>
    public const int TokenElevationClass = 20;

    public const uint MoveFileDelayUntilReboot = 0x00000004;

    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerCreateService = 0x0002;

    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const uint Delete = 0x00010000;
    public const uint ServiceAllAccess = 0x000F01FF;

    public const uint ServiceWin32OwnProcess = 0x00000010;
    public const uint ServiceAutoStart = 0x00000002;
    public const uint ServiceErrorNormal = 0x00000001;

    public const uint ServiceControlStop = 0x00000001;

    public const uint ServiceStopped = 1;
    public const uint ServiceStartPending = 2;
    public const uint ServiceStopPending = 3;
    public const uint ServiceRunning = 4;

    /// <summary>SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO.</summary>
    public const int ScStatusProcessInfo = 0;

    public const uint ServiceConfigDescription = 1;
    public const uint ServiceConfigFailureActions = 2;
    public const uint ServiceConfigServiceSidInfo = 5;

    /// <summary>SERVICE_SID_TYPE_UNRESTRICTED: the service gets its own SID (NT SERVICE\BlindsDownHelper).</summary>
    public const uint ServiceSidTypeUnrestricted = 1;

    public const int ScActionNone = 0;
    public const int ScActionRestart = 1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true)]
    public static partial nint CreateNamedPipeW(
        char* name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        SecurityAttributes* securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    public static partial nint CreateFileW(
        char* fileName,
        uint desiredAccess,
        uint shareMode,
        SecurityAttributes* securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true)]
    public static partial int WaitNamedPipeW(char* name, uint timeout);

    [LibraryImport("kernel32.dll", EntryPoint = "SetNamedPipeHandleState", SetLastError = true)]
    public static partial int SetNamedPipeHandleState(nint pipe, uint* mode, uint* maxCollectionCount, uint* collectDataTimeout);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    public static partial int GetNamedPipeServerProcessId(nint pipe, uint* serverProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeServerSessionId", SetLastError = true)]
    public static partial int GetNamedPipeServerSessionId(nint pipe, uint* serverSessionId);

    /// <summary>With no buffer: only whether the pipe is still connected. It fails with ERROR_BROKEN_PIPE once the
    /// other end has closed its handle, and never takes data out of the pipe.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "PeekNamedPipe", SetLastError = true)]
    public static partial int PeekNamedPipe(SafePipeHandle pipe, void* buffer, uint bufferSize, uint* bytesRead, uint* totalBytesAvailable, uint* bytesLeftThisMessage);

    /// <summary>Creates the named mutex with the security in <paramref name="attributes"/>, or opens the one that
    /// already exists (its security is then left as it is, and the last error is ERROR_ALREADY_EXISTS). Returns 0 on
    /// failure.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexExW", SetLastError = true)]
    public static partial nint CreateMutexExW(SecurityAttributes* attributes, char* name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    public static partial int ReadFile(nint file, byte* buffer, uint bytesToRead, uint* bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    public static partial int WriteFile(nint file, byte* buffer, uint bytesToWrite, uint* bytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetOverlappedResult", SetLastError = true)]
    public static partial int GetOverlappedResult(nint file, NativeOverlapped* overlapped, uint* bytesTransferred, int wait);

    [LibraryImport("kernel32.dll", EntryPoint = "CancelIoEx", SetLastError = true)]
    public static partial int CancelIoEx(nint file, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    public static partial int GetExitCodeProcess(nint process, uint* exitCode);

    /// <summary>With <see cref="MoveFileDelayUntilReboot"/> and a null new name: delete at the next restart
    /// (needs administrator rights; Windows records it in the registry's PendingFileRenameOperations).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true)]
    public static partial int MoveFileExW(char* existingFileName, char* newFileName, uint flags);

    /// <summary>The descriptor is allocated with LocalAlloc; free it with <see cref="LocalFree"/>.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true)]
    public static partial int ConvertStringSecurityDescriptorToSecurityDescriptorW(char* sddl, uint revision, void** securityDescriptor, uint* size);

    /// <summary>Returns a Win32 error code (no last-error). The owner points into <paramref name="securityDescriptor"/>,
    /// which must be freed with <see cref="LocalFree"/>.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    public static partial uint GetSecurityInfo(
        nint handle,
        int objectType,
        uint securityInfo,
        void** owner,
        void** group,
        void** dacl,
        void** sacl,
        void** securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    public static partial int OpenProcessToken(nint process, uint desiredAccess, nint* token);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    public static partial int GetTokenInformation(nint token, int informationClass, void* information, uint length, uint* returnLength);

    /// <summary>Always called with a null machine name: the local service control manager only.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    public static partial nint OpenSCManagerW(char* machineName, char* databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true)]
    public static partial nint OpenServiceW(nint serviceManager, char* serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true)]
    public static partial nint CreateServiceW(
        nint serviceManager,
        char* serviceName,
        char* displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        char* binaryPathName,
        char* loadOrderGroup,
        uint* tagId,
        char* dependencies,
        char* serviceStartName,
        char* password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    public static partial int ChangeServiceConfigW(
        nint service,
        uint serviceType,
        uint startType,
        uint errorControl,
        char* binaryPathName,
        char* loadOrderGroup,
        uint* tagId,
        char* dependencies,
        char* serviceStartName,
        char* password,
        char* displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    public static partial int ChangeServiceConfig2W(nint service, uint infoLevel, void* info);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    public static partial int StartServiceW(nint service, uint argumentCount, char** arguments);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    public static partial int ControlService(nint service, uint control, ServiceStatus* status);

    /// <summary>Unicode-neutral export (no A/W pair).</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    public static partial int DeleteService(nint service);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    public static partial int QueryServiceStatusEx(nint service, int infoLevel, byte* buffer, uint bufferSize, uint* bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    public static partial int QueryServiceConfigW(nint service, byte* buffer, uint bufferSize, uint* bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    public static partial int CloseServiceHandle(nint serviceHandle);

    /// <summary>SECURITY_ATTRIBUTES.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public uint Length;
        public void* SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>SERVICE_STATUS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
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

    /// <summary>QUERY_SERVICE_CONFIGW. The strings point into the same buffer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public char* BinaryPathName;
        public char* LoadOrderGroup;
        public uint TagId;
        public char* Dependencies;
        public char* ServiceStartName;
        public char* DisplayName;
    }

    /// <summary>SERVICE_DESCRIPTIONW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceDescription
    {
        public char* Description;
    }

    /// <summary>SC_ACTION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    /// <summary>SERVICE_FAILURE_ACTIONSW. Null strings leave the reboot message and command unset.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public char* RebootMessage;
        public char* Command;
        public uint ActionCount;
        public ScAction* Actions;
    }

    /// <summary>SERVICE_SID_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceSidInfo
    {
        public uint ServiceSidType;
    }
}
