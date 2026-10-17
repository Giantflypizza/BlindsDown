using BlindsDown.Core.IO;
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
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths paths = AppPaths.ForCurrentUser();
        Log.Initialize(paths.LogFile);

        string command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
        InstanceCommand? forward = command switch
        {
            "--toggle" => InstanceCommand.Toggle,
            "--lock" => InstanceCommand.Lock,
            "--unlock" or "--restore" => InstanceCommand.Unlock,
            "" or "--autostart" or "--show" => InstanceCommand.Show,
            _ => null,
        };

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        if (command == "--snapshot")
        {
            return CommandLineTools.WriteSnapshot(args.Length > 1 ? args[1] : null, paths);
        }

        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another BlindsDown is running in this Windows session: hand the command over and quit.
            if (forward is not null)
            {
                if (forward == InstanceCommand.Show)
                {
                    // This launch came from the user (Start menu, Explorer), so it may bring a window to the
                    // front; pass that right on, or the running instance's window would open behind others.
                    _ = Interop.SystemApis.SystemNative.AllowSetForegroundWindow(Interop.SystemApis.SystemNative.AsfwAny);
                }

                SingleInstance.SendToRunningInstance(forward.Value);
                return 0;
            }

            return 3; // e.g. --selftest needs the tray app to be closed.
        }

        if (command == "--restore")
        {
            return CommandLineTools.EmergencyRestore(paths);
        }

        if (command == "--selftest")
        {
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
}
