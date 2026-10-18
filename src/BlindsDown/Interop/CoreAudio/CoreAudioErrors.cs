using System.Globalization;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>
/// HRESULTs that Core Audio returns, and how BlindsDown reacts to them. Every Core Audio method is declared
/// with [PreserveSig], so failures arrive as these codes and never as surprise exceptions from the marshaller.
/// </summary>
internal static class CoreAudioErrors
{
    public const int SOk = 0;

    /// <summary>HRESULT_FROM_WIN32(ERROR_NOT_FOUND): no such endpoint, or no default device for a role.</summary>
    public const int NotFound = unchecked((int)0x80070490);

    public const int NoInterface = unchecked((int)0x80004002);

    /// <summary>
    /// AUDCLNT_S_NO_SINGLE_PROCESS. A SUCCESS code from IAudioSessionControl2::GetProcessId: the session spans
    /// several processes. The PID it returns is still the process that created the session.
    /// </summary>
    public const int NoSingleProcess = 0x0889000D;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the endpoint was removed, disabled or reset since the object was made.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>AUDCLNT_E_SERVICE_NOT_RUNNING: the Windows Audio service is stopped or restarting.</summary>
    public const int ServiceNotRunning = unchecked((int)0x88890010);

    /// <summary>E_ACCESSDENIED.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>HRESULT_FROM_WIN32(ERROR_PRIVILEGE_NOT_HELD): the caller lacks a privilege only administrators have.</summary>
    public const int PrivilegeNotHeld = unchecked((int)0x80070522);

    /// <summary>E_NOTIMPL.</summary>
    public const int NotImplemented = unchecked((int)0x80004001);

    /// <summary>REGDB_E_CLASSNOTREG: the COM class doesn't exist on this Windows.</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    private const int FileNotFound = unchecked((int)0x80070002);
    private const int DeviceNotConnected = unchecked((int)0x8007048F);
    private const int RpcServerDied = unchecked((int)0x80010007);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcServerDiedNoExecution = unchecked((int)0x80010012);
    private const int ObjectNotConnected = unchecked((int)0x800401FD);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcCallFailed = unchecked((int)0x800706BE);
    private const int RpcCallFailedNoExecution = unchecked((int)0x800706BF);

    /// <summary>
    /// True for errors that mean the audio service behind our objects went away (it was restarted, or it is
    /// stopped). Objects created before a restart stay broken forever, so the fix is to create them again.
    /// </summary>
    public static bool IsServiceFailure(int hr) => hr is ServiceNotRunning
        or RpcServerDied or RpcDisconnected or RpcServerDiedNoExecution or ObjectNotConnected
        or RpcServerUnavailable or RpcCallFailed or RpcCallFailedNoExecution;

    /// <summary>True for errors that mean this one endpoint is not usable right now (unplugged, removed, gone).</summary>
    public static bool IsDeviceUnavailable(int hr) => hr is NotFound or FileNotFound or DeviceNotConnected or DeviceInvalidated;

    /// <summary>Throws a <see cref="CoreAudioException"/> for a failure code; success codes (including S_FALSE) pass.</summary>
    public static void Check(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new CoreAudioException(operation, hr);
        }
    }

    /// <summary>A short explanation for logs and alerts, e.g. "the Windows audio service is not responding (0x88890010)".</summary>
    public static string Describe(int hr)
    {
        string code = "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
        if (IsServiceFailure(hr))
        {
            return $"the Windows audio service is not responding ({code})";
        }

        if (IsDeviceUnavailable(hr))
        {
            return $"the device is not available; it may have been unplugged or disabled ({code})";
        }

        return hr == AccessDenied
            ? $"Windows denied access ({code})"
            : $"Windows reported error {code}";
    }
}
