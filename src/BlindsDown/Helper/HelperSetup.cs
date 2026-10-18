using System.ComponentModel;
using System.Diagnostics;
using BlindsDown.Core.IO;
using BlindsDown.Infrastructure;
using BlindsDown.Usage;

namespace BlindsDown.Helper;

/// <summary>What the tray app knows about the helper without asking it.</summary>
/// <param name="QueryFailed">Windows couldn't be asked: nothing is known (it is NOT "not installed").</param>
internal sealed record HelperInstallInfo(bool Installed, bool Running, string? InstalledVersion, bool QueryFailed = false);

/// <summary>
/// [Stage 2 service] Installs, updates and removes the helper. Install, Update and Uninstall run in the ELEVATED
/// process started by <see cref="ElevatedLauncher"/> ("--install-helper" etc.) and refuse to run unelevated.
///  * Install: copies the running exe to <see cref="HelperPaths.InstalledExe"/> (Program Files, admin-only),
///    creates <see cref="HelperPaths.DataDirectory"/> with a protected DACL (SYSTEM and Administrators only),
///    registers the service "BlindsDownHelper" (LocalSystem, automatic start, restart on failure, description),
///    starts it, waits until the pipe answers, and adds an "Installed apps" entry whose uninstall runs
///    --uninstall-helper. If anything fails, everything done so far is rolled back.
///  * Update: the same exe copy plus a service restart; a lock in progress is kept (journal).
///  * Uninstall: asks the helper to unlock first (so every camera is re-enabled), stops and deletes the service,
///    removes the files (an exe in use is deleted at the next restart), the data folder and the Installed apps
///    entry. If the unlock fails, removal still continues: the devices come back at the next restart anyway, and
///    the exit code says so.
/// Exit codes: 0 ok, 1 failed (details in <see cref="HelperPaths.SetupLogFile"/>), 2 not elevated,
/// 3 removed but devices could not be re-enabled (they come back at the next restart).
/// </summary>
/// <remarks>
/// The steps and their rollback are in <see cref="HelperInstaller"/>; the Windows side in
/// <see cref="WindowsHelperSystem"/>. Uninstall started unelevated (from "Installed apps", which runs the uninstall
/// command as the signed-in user) starts itself again elevated, with one permission prompt, and returns that run's
/// exit code; a marker object makes sure a relaunch that still isn't elevated (UAC turned off) never relaunches again.
/// Elevated runs take turns (<see cref="HelperSetupTurn"/>): a second one waits for the first to end.
/// A failed install keeps the data folder: it holds only the setup log that explains the failure.
/// The tray app may follow the option with "--request-id" and an ID of its own (see <see cref="SetupRequestIds"/>):
/// the run's unlock requests carry it, so the tray app knows that unlock is its own install's or removal's and
/// doesn't report it as another program's. Without one (from "Installed apps", or by hand) the run is the same.
/// </remarks>
internal static class HelperSetup
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitNotElevated = 2;
    public const int ExitDevicesNotRestored = 3;

    /// <summary>Per Windows session ("Local\"), like <see cref="SingleInstance"/>'s objects.</summary>
    private const string RelaunchMarkerName = @"Local\BlindsDown-6c2a3f9e-UninstallHelperRelaunch";

    /// <summary>Longer than the slowest run (an unlock of many devices, then stopping and starting the service).</summary>
    private static readonly TimeSpan SetupWait = TimeSpan.FromMinutes(5);

    /// <param name="requestId">From the tray app (see <see cref="RequestIdFromArguments"/>), or null.</param>
    public static int Install(string? requestId = null) => RunElevated("install", HelperInstaller.Install, requestId);

    /// <param name="requestId">From the tray app (see <see cref="RequestIdFromArguments"/>), or null.</param>
    public static int Update(string? requestId = null) => RunElevated("update", HelperInstaller.Update, requestId);

    /// <param name="requestId">From the tray app (see <see cref="RequestIdFromArguments"/>), or null. An unelevated run
    /// passes it on to the elevated one it starts.</param>
    public static int Uninstall(string? requestId = null) =>
        ProcessElevation.IsElevated() ? RunElevated("remove", HelperInstaller.Uninstall, requestId) : RelaunchUninstallElevated(requestId);

    /// <summary>
    /// The request ID on a setup run's command line: the option (args[0]), then exactly "--request-id" and 32
    /// lowercase hex characters, as <see cref="ElevatedLauncher"/> passes them. Anything else (no ID, a malformed one,
    /// more arguments) is no ID: the run does exactly what it did before IDs existed, and only the tray app's
    /// "unlocked by another program" check can't recognise its unlock.
    /// </summary>
    internal static string? RequestIdFromArguments(IReadOnlyList<string> args) =>
        args.Count == 3
        && string.Equals(args[1], ElevatedLauncher.RequestIdOption, StringComparison.Ordinal)
        && ElevatedLauncher.IsRequestId(args[2])
            ? args[2]
            : null;

    /// <summary>
    /// The IDs a setup run given <paramref name="requestId"/> sends with its unlock requests, in order:
    /// <paramref name="requestId"/>, then <paramref name="requestId"/>-2 and <paramref name="requestId"/>-3. The tray
    /// app registers all of them as its own before it starts the run. One ID isn't enough: the helper refuses an ID it
    /// has seen, a removal can unlock twice (a helper that doesn't answer is started and asked again), and an attempt
    /// that got no answer may still have reached the helper. Each attempt takes the next ID whatever happened to the one
    /// before; after the third a run sends none (no path unlocks that often). All three are valid request IDs (34
    /// characters at most: letters, digits and '-').
    /// Most runs use fewer (an update none): once the tray app has seen the run's changes it should stop counting the
    /// unused ones as its own. They are on the run's command line and easy to work out from each other, so a program
    /// that learned them could otherwise unlock later without the "unlocked by another program" alarm.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is not 32 lowercase hex characters.</exception>
    internal static IReadOnlyList<string> SetupRequestIds(string requestId) =>
        ElevatedLauncher.IsRequestId(requestId)
            ? [requestId, requestId + "-2", requestId + "-3"]
            : throw new ArgumentException($"A setup request ID must be {ElevatedLauncher.RequestIdLength} lowercase hex characters.", nameof(requestId));

    /// <summary>Unelevated: is the service registered, is it running, and which version is its exe.</summary>
    public static HelperInstallInfo Query()
    {
        try
        {
            HelperServiceState state = HelperServiceControl.Query();
            return state.Installed
                ? new HelperInstallInfo(true, state.Running, ReadVersion(HelperServiceControl.ExePathFromBinaryPath(state.BinaryPath)))
                : new HelperInstallInfo(false, false, null);
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"Could not ask Windows about the camera helper: {ex.Message}");
            return new HelperInstallInfo(false, false, null, QueryFailed: true);
        }
    }

    /// <summary>"0.2.0+3f1c2ab" (an exe's product version) -> "0.2.0", the form of <see cref="AppVersion.Text"/>.</summary>
    internal static string? VersionText(string? productVersion)
    {
        string text = productVersion?.Trim() ?? string.Empty;
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>The version of the helper's exe. Read only from a local disk (the path comes from the service's
    /// configuration; a network path would mean network traffic).</summary>
    private static string? ReadVersion(string? exePath)
    {
        if (exePath is null || !ImagePaths.IsSafeToOpen(exePath))
        {
            return null;
        }

        try
        {
            return VersionText(FileVersionInfo.GetVersionInfo(exePath).ProductVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static int RunElevated(string what, Func<IHelperSystem, int> run, string? requestId)
    {
        // This account's own BlindsDown log until the helper's data folder is ready. The network tripwire writes to
        // whichever log is in use (this process has no tray icon to show an alert).
        Log.Initialize(AppPaths.ForCurrentUser().LogFile);
        NetworkTripwire.InstallLogOnly();
        if (!ProcessElevation.IsElevated())
        {
            Log.Warn($"Can't {what} the camera helper without administrator rights (BlindsDown starts this step with a Windows permission prompt).");
            return ExitNotElevated;
        }

        using HelperSetupTurn? turn = HelperSetupTurn.TryTake(HelperSetupTurn.MutexName, SetupWait);
        if (turn is null)
        {
            Log.Error($"Can't {what} the camera helper now: another install, update or removal of it is still running.");
            return ExitFailed;
        }

        // The data folder before its log, created with its own security, because starting the log there would
        // create it with ProgramData's default permissions.
        DataDirectoryResult data = HelperDataDirectory.Prepare(HelperPaths.DataDirectory);
        if (!data.Ready)
        {
            Log.Error($"Can't {what} the camera helper: its data folder {HelperPaths.DataDirectory} could not be prepared: {data.Problem}");
            return ExitFailed;
        }

        Log.Initialize(HelperPaths.SetupLogFile);
        Log.Info($"BlindsDown {AppVersion.Text}: {what} the camera helper (from {Environment.ProcessPath}"
            + (ElevatedLauncher.IsRequestId(requestId) ? ", with a request ID from the tray app)." : ")."));
        if (data.MovedAside is not null)
        {
            Log.Warn($"{HelperPaths.DataDirectory} was not BlindsDown's (others could change it); moved it to {data.MovedAside} and created a new one.");
        }

        int exitCode;
        try
        {
            exitCode = run(new WindowsHelperSystem(requestId));
        }
        catch (Exception ex)
        {
            // The steps roll themselves back; this only keeps an unexpected error from crashing the setup run.
            Log.Error($"Unexpected error while trying to {what} the camera helper", ex);
            exitCode = ExitFailed;
        }

        Log.Info($"Done ({what}): exit code {exitCode}.");
        return exitCode;
    }

    private static int RelaunchUninstallElevated(string? requestId)
    {
        Log.Initialize(AppPaths.ForCurrentUser().LogFile);
        NetworkTripwire.InstallLogOnly();
        if (RelaunchMarkerExists())
        {
            Log.Warn("Removing the camera helper needs administrator rights, and Windows didn't grant them.");
            return ExitNotElevated;
        }

        using var marker = new Mutex(initiallyOwned: false, RelaunchMarkerName, out bool createdNew);
        if (!createdNew)
        {
            return ExitNotElevated;
        }

        Log.Info("Removing the camera helper needs administrator rights; asking Windows for them.");
        string? id = ElevatedLauncher.IsRequestId(requestId) ? requestId : null;
        int? exitCode = ElevatedLauncher.RunAsync(ElevatedTask.UninstallHelper, owner: null, id).GetAwaiter().GetResult();
        return exitCode ?? ExitNotElevated;
    }

    private static bool RelaunchMarkerExists()
    {
        try
        {
            if (Mutex.TryOpenExisting(RelaunchMarkerName, out Mutex? existing))
            {
                existing.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
