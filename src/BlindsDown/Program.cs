using BlindsDown.Core.IO;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown;

/// <summary>
/// Entry point. Command line:
///   (no arguments)        start the tray app (or show the running one)
///   --autostart           same, started by Windows at sign-in
///   --toggle / --lock / --unlock   send the command to the running BlindsDown (starts it if needed)
///   --restore             emergency: undo every change BlindsDown made, then exit
///   --snapshot [file]     write the state of every microphone to a text file, then exit
///   --selftest [file]     lock, verify, unlock and verify everything is exactly as before (mutes the
///                         microphones for about a second), write a report, then exit
/// The optional camera helper (one exe, more modes; see Helper/):
///   --service             run as the helper service (started by Windows, never by hand)
///   --install-helper / --update-helper / --uninstall-helper [--request-id <32 hex>]
///                         install, update or remove the helper (need administrator rights; the tray app
///                         starts them elevated, with an ID for the unlock requests they send, and
///                         "Installed apps" runs --uninstall-helper)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
        switch (mode)
        {
            // These run without the tray app's per-user folder: as SYSTEM, or as an administrator account that
            // may not be the signed-in user. They set up their own logging under ProgramData.
            case "--service":
                return HelperService.Run();
            case "--install-helper":
                return HelperSetup.Install(HelperSetup.RequestIdFromArguments(args));
            case "--update-helper":
                return HelperSetup.Update(HelperSetup.RequestIdFromArguments(args));
            case "--uninstall-helper":
                return HelperSetup.Uninstall(HelperSetup.RequestIdFromArguments(args));
        }

        AppPaths paths = AppPaths.ForCurrentUser();
        Log.Initialize(paths.LogFile);

        string command = mode;
        (InstanceCommand? forward, bool runsTrayApp) = Plan(command);

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        if (command == "--snapshot")
        {
            NetworkTripwire.InstallLogOnly();
            return CommandLineTools.WriteSnapshot(args.Length > 1 ? args[1] : null, paths);
        }

        SingleInstance? acquired = SingleInstance.TryAcquire(takesCommands: runsTrayApp);
        if (acquired is null)
        {
            // Another BlindsDown is running in this Windows session: hand the command over and quit.
            if (forward is null)
            {
                return 3; // e.g. --selftest needs the tray app to be closed.
            }

            if (forward == InstanceCommand.Show)
            {
                // This launch came from the user (Start menu, Explorer), so it may bring a window to the
                // front; pass that right on, or the running instance's window would open behind others.
                _ = Interop.SystemApis.SystemNative.AllowSetForegroundWindow(Interop.SystemApis.SystemNative.AsfwAny);
            }

            if (SingleInstance.SendToRunningInstance(forward.Value))
            {
                return 0;
            }

            // The running BlindsDown takes no commands: it is exiting, or it is --restore or --selftest. Never pretend it
            // worked: once it has ended, the command is carried out here, as if it had never been running.
            Log.Warn($"Could not pass {forward.Value} to the running BlindsDown; waiting for it to end.");
            acquired = WaitForInstanceToEnd(forward.Value, runsTrayApp, out bool delivered);
            if (delivered)
            {
                return 0;
            }

            if (acquired is null)
            {
                // The user is told, unless Windows started this one at sign-in: nobody waits for that, so only the log tells.
                Log.Warn($"The running BlindsDown didn't end in time, so {forward.Value} was not carried out.");
                if (command != "--autostart")
                {
                    MessageBox.Show(
                        NotTakenText(command),
                        command == "--restore" ? "BlindsDown – restore" : "BlindsDown",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                return 1;
            }
        }

        using SingleInstance instance = acquired;
        if (command == "--restore")
        {
            NetworkTripwire.InstallLogOnly();
            return CommandLineTools.EmergencyRestore(paths);
        }

        if (command == "--selftest")
        {
            NetworkTripwire.InstallLogOnly();
            return CommandLineTools.SelfTest(args.Length > 1 ? args[1] : null, paths);
        }

        if (forward is null)
        {
            MessageBox.Show(
                $"Unknown option \"{args[0]}\".\n\nOptions: --toggle, --lock, --unlock, --restore, --snapshot [file], --selftest [file]",
                "BlindsDown",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 2;
        }

        Log.Info($"BlindsDown {AppVersion.Text} starting ({command})");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        using var host = new AppHost(paths, instance, startupCommand: forward.Value, autostarted: command == "--autostart");
        Application.Run(host);
        Log.Info("BlindsDown exited");
        return 0;
    }

    /// <summary>
    /// What a launch does with its command: the command it hands to a BlindsDown that is already running (null: none, it
    /// has to run alone), and whether it runs the tray app itself, the only mode that takes commands from later launches.
    /// --restore hands over an unlock, but when it runs alone it restores and exits.
    /// </summary>
    internal static (InstanceCommand? Forward, bool RunsTrayApp) Plan(string command) => command switch
    {
        "--toggle" => (InstanceCommand.Toggle, true),
        "--lock" => (InstanceCommand.Lock, true),
        "--unlock" => (InstanceCommand.Unlock, true),
        "--restore" => (InstanceCommand.Unlock, false),
        "" or "--autostart" or "--show" => (InstanceCommand.Show, true),
        _ => (null, false),
    };

    /// <summary>What the user is told when the BlindsDown that is running took no commands and didn't end within the
    /// wait: it hangs while exiting, or --restore still shows its message (or --selftest still runs).</summary>
    internal static string NotTakenText(string command)
    {
        string result = command switch
        {
            "--restore" => "BlindsDown didn't restore anything",
            "--lock" => "BlindsDown didn't lock",
            "--unlock" => "BlindsDown didn't unlock",
            "--toggle" => "BlindsDown didn't lock or unlock",
            _ => "BlindsDown didn't start",
        };
        return result + ": another BlindsDown is running but isn't taking requests. It may still be closing, or showing a message "
            + "(for example from --restore). Close that message, or end BlindsDown in Task Manager, then try again.";
    }

    /// <summary>
    /// The BlindsDown that is running takes no commands: it is exiting (it stops taking them a few seconds before it is
    /// gone), or it is a command-line mode such as --restore. Waits up to 15 s for it to end, and returns this process's
    /// own instance then. Returns null when it is still there, or when another launch started BlindsDown again meanwhile
    /// and the command was handed to that one after all (<paramref name="delivered"/>).
    /// </summary>
    private static SingleInstance? WaitForInstanceToEnd(InstanceCommand command, bool takesCommands, out bool delivered)
    {
        delivered = false;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (SingleInstance.TryAcquire(takesCommands) is SingleInstance own)
            {
                return own;
            }

            if (SingleInstance.SendToRunningInstance(command))
            {
                delivered = true;
                return null;
            }
        }

        return null;
    }
}
