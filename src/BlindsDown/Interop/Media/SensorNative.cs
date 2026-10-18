using System.Runtime.InteropServices;

namespace BlindsDown.Interop.Media;

/// <summary>
/// The flat Media Foundation functions the camera activity monitor needs. Neither DLL is a KnownDLL, so both are
/// loaded from System32 only (never from the app's folder). On Windows editions without Media Foundation (N/KN
/// editions without the Media Feature Pack) the first call throws DllNotFoundException; the caller treats that as
/// "not available".
/// </summary>
internal static partial class SensorNative
{
    /// <summary>MF_VERSION: (MF_SDK_VERSION 0x0002 &lt;&lt; 16) | MF_API_VERSION 0x0070.</summary>
    public const uint MfVersion = 0x00020070;

    /// <summary>
    /// MFSTARTUP_LITE (= MFSTARTUP_NOSOCKET): start Media Foundation without its sockets library. The sensor
    /// monitor needs none of it, and BlindsDown never wants network code initialised.
    /// </summary>
    public const uint MfStartupLite = 0x1;

    /// <summary>Reference-counted per process: every successful call needs one <see cref="MFShutdown"/>.</summary>
    [LibraryImport("mfplat.dll", EntryPoint = "MFStartup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll", EntryPoint = "MFShutdown")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int MFShutdown();

    /// <summary>
    /// <paramref name="callback"/> is an IMFSensorActivitiesReportCallback pointer (the monitor keeps its own
    /// reference); <paramref name="monitor"/> receives an IMFSensorActivityMonitor the caller owns.
    /// </summary>
    [LibraryImport("mfsensorgroup.dll", EntryPoint = "MFCreateSensorActivityMonitor")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int MFCreateSensorActivityMonitor(nint callback, out nint monitor);
}
