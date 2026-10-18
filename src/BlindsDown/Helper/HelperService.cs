using System.Diagnostics;
using System.ServiceProcess;
using BlindsDown.Core.IO;
using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>
/// [Stage 2 service] The optional helper: "BlindsDown.exe --service", a Windows service running as LocalSystem
/// (installed by <see cref="HelperSetup"/>). It owns a <see cref="Core.Pnp.DeviceLockEngine"/> with the real
/// <see cref="Devices.Pnp.CfgMgrPnpBackend"/> on its own <see cref="Infrastructure.EngineThread"/>, a
/// <see cref="Devices.Pnp.PnpNotificationWatcher"/>, a 3-second Reconcile timer and the
/// <see cref="HelperPipeServer"/>, which hands each request to a <see cref="Core.Helper.HelperRequestHandler"/> on
/// the engine thread. Logs to <see cref="HelperPaths.LogFile"/>.
/// Start: Initialize (re-applies a journaled lock, so the lock survives a restart). Stop and shutdown keep the
/// lock (the journal stays) and just stop listening. No network, no UI.
/// </summary>
/// <remarks>
/// Only Windows' service manager may start it. Services run in session 0, where nothing a signed-in user starts
/// ever runs; started any other way (say, double-clicked with --service) it writes one line to the user's own
/// BlindsDown log and exits with 1, without touching the helper's data folder.
/// </remarks>
internal static class HelperService
{
    /// <summary>Entry point for --service. Returns the process exit code.</summary>
    public static int Run()
    {
        if (!IsInServicesSession())
        {
            Log.Initialize(AppPaths.ForCurrentUser().LogFile);
            Log.Warn("\"--service\" is only for Windows' service manager, which starts the camera helper; it was started some other way and exits.");
            return 1;
        }

        // The data folder first, with its own security, and checked on every start: the journal in it tells this
        // SYSTEM service which devices to turn off and on, so only SYSTEM and Administrators may change it.
        DataDirectoryResult data = HelperDataDirectory.Prepare(HelperPaths.DataDirectory);
        if (!data.Ready)
        {
            // Nowhere trustworthy to log to, and no trustworthy journal: refuse to run. Devices it may have turned
            // off come back at the next restart (the disable never persists).
            return 1;
        }

        Log.Initialize(HelperPaths.LogFile);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception in the helper", e.ExceptionObject as Exception);

        // The same promise as the tray app's: networking code loading into this process is logged (there is no
        // screen in session 0 for an alert). It also reports what loaded before this line.
        NetworkTripwire.InstallLogOnly();

        if (data.MovedAside is not null)
        {
            Log.Warn($"{HelperPaths.DataDirectory} could be changed by others; moved it to {data.MovedAside} and created a new one. A lock recorded there is not used.");
        }

        using var host = new HelperServiceHost();
        ServiceBase.Run(host);
        if (!host.WasStarted)
        {
            Log.Warn("\"--service\" was not started by Windows' service manager; exiting.");
            return 1;
        }

        return 0;
    }

    private static bool IsInServicesSession()
    {
        using Process self = Process.GetCurrentProcess();
        return self.SessionId == 0;
    }
}
