using System.ComponentModel;
using System.Runtime.InteropServices;
using BlindsDown.Core.Helper;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>What the service control manager says about the helper service.</summary>
/// <param name="Installed">The service is registered.</param>
/// <param name="Running">It is running (not stopped, starting or stopping).</param>
/// <param name="BinaryPath">Its command line ("\"C:\...\BlindsDown.exe\" --service"), when readable.</param>
internal sealed record HelperServiceState(bool Installed, bool Running, string? BinaryPath)
{
    public static HelperServiceState NotInstalled { get; } = new(false, false, null);
}

/// <summary>
/// The helper service in Windows' service control manager (advapi32), always on THIS computer (the machine name is
/// always null). Everything except <see cref="Query"/> needs administrator rights. Failures throw
/// <see cref="Win32Exception"/> with a message that says which step failed.
/// </summary>
internal static unsafe class HelperServiceControl
{
    /// <summary>"Restart after 5 s" three times, then stop restarting: a helper that crashes on every start
    /// should not loop forever. Failures are counted from zero again after a day without one.</summary>
    internal static readonly (int Type, uint DelayMs)[] FailureActions =
    [
        (HelperNative.ScActionRestart, 5000),
        (HelperNative.ScActionRestart, 5000),
        (HelperNative.ScActionRestart, 5000),
        (HelperNative.ScActionNone, 0),
    ];

    internal const uint FailureResetPeriodSeconds = 24 * 60 * 60;

    internal const string Description =
        "Turns cameras and microphone-only devices off and back on for BlindsDown's privacy lock. It never connects to the internet.";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The service's command line. Quoted, because Program Files has a space in it: unquoted, Windows
    /// would first try to run "C:\Program.exe".</summary>
    internal static string BinaryPath(string exePath)
    {
        if (exePath.Contains('"', StringComparison.Ordinal) || !Path.IsPathFullyQualified(exePath))
        {
            throw new ArgumentException("The helper's program path must be a full path without quotes.", nameof(exePath));
        }

        return $"\"{exePath}\" --service";
    }

    /// <summary>The program file in a service command line: the quoted part, or everything up to ".exe".</summary>
    internal static string? ExePathFromBinaryPath(string? commandLine)
    {
        string text = commandLine?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }

    /// <summary>Read-only; works unelevated (interactive users may query a service's status and configuration).
    /// Throws <see cref="Win32Exception"/> only for unexpected errors.</summary>
    public static HelperServiceState Query()
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle? service = TryOpenService(manager, HelperNative.ServiceQueryStatus | HelperNative.ServiceQueryConfig);
        if (service is null)
        {
            return HelperServiceState.NotInstalled;
        }

        return new HelperServiceState(true, CurrentState(service) == HelperNative.ServiceRunning, QueryBinaryPath(service));
    }

    public static bool Exists()
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle? service = TryOpenService(manager, HelperNative.ServiceQueryStatus);
        return service is not null;
    }

    public static bool IsRunning()
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle? service = TryOpenService(manager, HelperNative.ServiceQueryStatus);
        return service is not null && CurrentState(service) == HelperNative.ServiceRunning;
    }

    /// <summary>Registers the service: LocalSystem, own process, automatic start, plus <see cref="Configure2"/>. The
    /// command line is always the installed copy in Program Files with --service: no caller can pass another one.</summary>
    public static void Create()
    {
        string binaryPath = BinaryPath(HelperPaths.InstalledExe);
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect | HelperNative.ScManagerCreateService);
        nint raw;
        fixed (char* name = HelperProtocol.ServiceName)
        fixed (char* display = HelperProtocol.ServiceDisplayName)
        fixed (char* path = binaryPath)
        {
            // A null account name means LocalSystem.
            raw = HelperNative.CreateServiceW(
                manager.DangerousGetHandle(),
                name,
                display,
                HelperNative.ServiceAllAccess,
                HelperNative.ServiceWin32OwnProcess,
                HelperNative.ServiceAutoStart,
                HelperNative.ServiceErrorNormal,
                path,
                null,
                null,
                null,
                null,
                null);
        }

        using ServiceHandle service = Check(raw, "register the helper service");
        Configure2(service);
    }

    /// <summary>Re-applies the command line (always the installed copy, see <see cref="Create"/>), start type and the
    /// settings of <see cref="Configure2"/> (Update).</summary>
    public static void Configure()
    {
        string binaryPath = BinaryPath(HelperPaths.InstalledExe);
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle service = OpenService(manager, HelperNative.ServiceChangeConfig | HelperNative.ServiceStart | HelperNative.ServiceQueryConfig);
        fixed (char* path = binaryPath)
        fixed (char* display = HelperProtocol.ServiceDisplayName)
        {
            if (HelperNative.ChangeServiceConfigW(
                service.DangerousGetHandle(),
                HelperNative.ServiceWin32OwnProcess,
                HelperNative.ServiceAutoStart,
                HelperNative.ServiceErrorNormal,
                path,
                null,
                null,
                null,
                null,
                null,
                display) == 0)
            {
                throw Failure("update the helper service's settings");
            }
        }

        Configure2(service);
    }

    /// <summary>Starts the service (if it isn't running) and waits until it runs.</summary>
    public static void Start(TimeSpan timeout)
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle service = OpenService(manager, HelperNative.ServiceStart | HelperNative.ServiceQueryStatus);
        if (HelperNative.StartServiceW(service.DangerousGetHandle(), 0, null) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != HelperNative.ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error, $"Could not start the helper service: {new Win32Exception(error).Message}");
            }
        }

        HelperNative.ServiceStatusProcess status = WaitWhile(service, HelperNative.ServiceStartPending, timeout);
        if (status.CurrentState != HelperNative.ServiceRunning)
        {
            throw new Win32Exception(
                unchecked((int)status.Win32ExitCode),
                $"The helper service did not start (state {status.CurrentState}, exit code {status.Win32ExitCode}).");
        }
    }

    /// <summary>Stops the service (if it runs) and waits until it has stopped. Nothing to do when it isn't registered
    /// (an undo of a registration that failed).</summary>
    public static void Stop(TimeSpan timeout)
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle? service = TryOpenService(manager, HelperNative.ServiceStop | HelperNative.ServiceQueryStatus);
        if (service is null)
        {
            return;
        }

        int error = SendStop(service);
        if (error != 0)
        {
            uint state = CurrentState(service);
            if (state == HelperNative.ServiceStartPending)
            {
                // A service that is still starting can't take a stop yet: wait for it, then ask again.
                WaitWhile(service, HelperNative.ServiceStartPending, timeout);
                error = SendStop(service);
                state = error == 0 ? HelperNative.ServiceStopPending : CurrentState(service);
            }

            if (state == HelperNative.ServiceStopped)
            {
                return;
            }

            if (state != HelperNative.ServiceStopPending)
            {
                throw new Win32Exception(error, $"Could not stop the helper service: {new Win32Exception(error).Message}");
            }
        }

        HelperNative.ServiceStatusProcess status = WaitWhile(service, HelperNative.ServiceStopPending, timeout);
        if (status.CurrentState != HelperNative.ServiceStopped)
        {
            throw new Win32Exception(HelperNative.ErrorSemTimeout, "The helper service did not stop in time.");
        }
    }

    /// <summary>Removes the registration. Windows completes it once the service has stopped and every handle to it
    /// is closed.</summary>
    public static void Delete()
    {
        using ServiceHandle manager = OpenManager(HelperNative.ScManagerConnect);
        using ServiceHandle? service = TryOpenService(manager, HelperNative.Delete);
        if (service is null)
        {
            return;
        }

        if (HelperNative.DeleteService(service.DangerousGetHandle()) == 0 && Marshal.GetLastPInvokeError() != HelperNative.ErrorServiceMarkedForDelete)
        {
            throw Failure("remove the helper service");
        }
    }

    /// <summary>Description, restart on failure, and an own service SID (NT SERVICE\BlindsDownHelper).</summary>
    private static void Configure2(ServiceHandle service)
    {
        fixed (char* description = Description)
        {
            var info = new HelperNative.ServiceDescription { Description = description };
            if (HelperNative.ChangeServiceConfig2W(service.DangerousGetHandle(), HelperNative.ServiceConfigDescription, &info) == 0)
            {
                throw Failure("set the helper service's description");
            }
        }

        HelperNative.ScAction* actions = stackalloc HelperNative.ScAction[FailureActions.Length];
        for (int i = 0; i < FailureActions.Length; i++)
        {
            actions[i] = new HelperNative.ScAction { Type = FailureActions[i].Type, Delay = FailureActions[i].DelayMs };
        }

        var failure = new HelperNative.ServiceFailureActions
        {
            ResetPeriod = FailureResetPeriodSeconds,
            ActionCount = (uint)FailureActions.Length,
            Actions = actions,

            // Never a program to run on failure: the only program the helper may ever be is itself (a CI rule checks it).
            Command = null,
            RebootMessage = null,
        };
        if (HelperNative.ChangeServiceConfig2W(service.DangerousGetHandle(), HelperNative.ServiceConfigFailureActions, &failure) == 0)
        {
            throw Failure("set the helper service's restart-on-failure settings");
        }

        var sid = new HelperNative.ServiceSidInfo { ServiceSidType = HelperNative.ServiceSidTypeUnrestricted };
        if (HelperNative.ChangeServiceConfig2W(service.DangerousGetHandle(), HelperNative.ServiceConfigServiceSidInfo, &sid) == 0)
        {
            throw Failure("set the helper service's SID type");
        }
    }

    private static ServiceHandle OpenManager(uint access)
    {
        // A null machine name: this computer's service control manager, never another one's.
        return Check(HelperNative.OpenSCManagerW(null, null, access), "open the service control manager");
    }

    private static ServiceHandle OpenService(ServiceHandle manager, uint access) =>
        TryOpenService(manager, access) ?? throw new Win32Exception(HelperNative.ErrorServiceDoesNotExist, "The helper service is not installed.");

    private static ServiceHandle? TryOpenService(ServiceHandle manager, uint access)
    {
        nint raw;
        fixed (char* name = HelperProtocol.ServiceName)
        {
            raw = HelperNative.OpenServiceW(manager.DangerousGetHandle(), name, access);
        }

        if (raw == 0 && Marshal.GetLastPInvokeError() == HelperNative.ErrorServiceDoesNotExist)
        {
            return null;
        }

        return Check(raw, "open the helper service");
    }

    /// <summary>Returns 0, or the Windows error (ERROR_SERVICE_NOT_ACTIVE when it isn't running).</summary>
    private static int SendStop(ServiceHandle service)
    {
        HelperNative.ServiceStatus ignored;
        return HelperNative.ControlService(service.DangerousGetHandle(), HelperNative.ServiceControlStop, &ignored) != 0
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    private static uint CurrentState(ServiceHandle service) => QueryStatus(service).CurrentState;

    private static HelperNative.ServiceStatusProcess QueryStatus(ServiceHandle service)
    {
        HelperNative.ServiceStatusProcess status;
        uint needed;
        if (HelperNative.QueryServiceStatusEx(
            service.DangerousGetHandle(),
            HelperNative.ScStatusProcessInfo,
            (byte*)&status,
            (uint)sizeof(HelperNative.ServiceStatusProcess),
            &needed) == 0)
        {
            throw Failure("read the helper service's status");
        }

        return status;
    }

    private static string? QueryBinaryPath(ServiceHandle service)
    {
        uint needed = 0;
        if (HelperNative.QueryServiceConfigW(service.DangerousGetHandle(), null, 0, &needed) != 0
            || Marshal.GetLastPInvokeError() != HelperNative.ErrorInsufficientBuffer
            || needed == 0
            || needed > 64 * 1024)
        {
            return null;
        }

        byte[] buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (HelperNative.QueryServiceConfigW(service.DangerousGetHandle(), p, needed, &needed) == 0)
            {
                return null;
            }

            var config = (HelperNative.QueryServiceConfig*)p;
            return config->BinaryPathName is null ? null : new string(config->BinaryPathName);
        }
    }

    /// <summary>Polls while the service is in <paramref name="pendingState"/>; returns the last status.</summary>
    private static HelperNative.ServiceStatusProcess WaitWhile(ServiceHandle service, uint pendingState, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        HelperNative.ServiceStatusProcess status = QueryStatus(service);
        while (status.CurrentState == pendingState && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(PollInterval);
            status = QueryStatus(service);
        }

        return status;
    }

    private static ServiceHandle Check(nint raw, string what)
    {
        if (raw == 0)
        {
            throw Failure(what);
        }

        return new ServiceHandle(raw);
    }

    private static Win32Exception Failure(string what)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"Could not {what}: {new Win32Exception(error).Message}");
    }

    /// <summary>A service control manager or service handle.</summary>
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => HelperNative.CloseServiceHandle(handle) != 0;
    }
}
