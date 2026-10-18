using BlindsDown.Core.Helper;
using BlindsDown.Core.IO;
using BlindsDown.Core.Locking;
using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>The real <see cref="IHelperSystem"/>: Program Files, the service control manager, HKLM, the pipe.
/// Used only by the elevated setup runs.</summary>
internal sealed class WindowsHelperSystem : IHelperSystem
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Turning many devices back on can take a while, and an app holding a camera may delay it. Longer than
    /// the helper's own wait, so its answer (done, busy, or still working) always arrives.</summary>
    private static readonly TimeSpan UnlockTimeout = HelperTimeouts.ChangeTimeout;

    /// <summary>The IDs this run's unlock requests carry, in order (empty: none, as before IDs existed).</summary>
    private readonly IReadOnlyList<string> _unlockRequestIds;

    /// <summary>How many of <see cref="_unlockRequestIds"/> are used up.</summary>
    private int _unlockIdsUsed;

    /// <param name="requestId">The ID the tray app handed this setup run (see <see cref="HelperSetup.SetupRequestIds"/>),
    /// or null. Anything but 32 lowercase hex characters counts as none.</param>
    public WindowsHelperSystem(string? requestId = null)
    {
        _unlockRequestIds = ElevatedLauncher.IsRequestId(requestId) ? HelperSetup.SetupRequestIds(requestId) : [];
    }

    public bool IsElevated => ProcessElevation.IsElevated();

    private static string BinaryPath => HelperServiceControl.BinaryPath(HelperPaths.InstalledExe);

    public bool ServiceExists() => HelperServiceControl.Exists();

    public bool IsServiceRunning() => HelperServiceControl.IsRunning();

    public bool HasJournal() => AtomicFile.AnyCopyExists(HelperPaths.JournalFile);

    public IInstalledFiles InstallFiles() => StagedInstall.Apply(HelperFiles.CurrentPlan(), HelperPaths.InstallDirectory);

    public void CreateService() => HelperServiceControl.Create();

    public void ConfigureService() => HelperServiceControl.Configure();

    public void StartService() => HelperServiceControl.Start(ServiceTimeout);

    public void StopService() => HelperServiceControl.Stop(ServiceTimeout);

    public void DeleteService() => HelperServiceControl.Delete();

    public bool WaitUntilHelperAnswers(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var client = new HelperClient();
        var hello = new HelperRequest { Command = HelperCommand.Hello, ClientVersion = AppVersion.Text, Source = "setup" };
        while (true)
        {
            try
            {
                HelperResponse response = client.SendAsync(hello, HelloTimeout).GetAwaiter().GetResult();
                Log.Info($"The helper answers (version {response.HelperVersion}).");
                if (!response.Ok)
                {
                    Log.Warn($"The helper reports a problem: {response.Error}");
                }

                if (response.HelperVersion != AppVersion.Text)
                {
                    Log.Warn($"The helper answering is version {response.HelperVersion}, not {AppVersion.Text}.");
                }

                return true;
            }
            catch (HelperUnavailableException ex)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    Log.Warn($"The helper did not answer: {ex.Message}");
                    return false;
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }

    public HelperUnlockOutcome UnlockDevices()
    {
        try
        {
            return UnlockOutcome(new HelperClient().SendAsync(NextUnlockRequest(), UnlockTimeout).GetAwaiter().GetResult());
        }
        catch (HelperUnavailableException ex)
        {
            Log.Warn($"Could not reach the helper to unlock: {ex.Message}");
            return HelperUnlockOutcome.Unreachable;
        }
    }

    /// <summary>
    /// The next unlock request of this run, with the next of its IDs (see <see cref="HelperSetup.SetupRequestIds"/>),
    /// or without an ID once they are used up or when the run has none. An ID is used up when its request is built,
    /// whatever the answer: one that got none may still have reached the helper, which then refuses the ID again.
    /// </summary>
    internal HelperRequest NextUnlockRequest()
    {
        string? requestId = null;
        if (_unlockIdsUsed < _unlockRequestIds.Count)
        {
            requestId = _unlockRequestIds[_unlockIdsUsed];
            _unlockIdsUsed++;
        }

        return new HelperRequest { Command = HelperCommand.Unlock, ClientVersion = AppVersion.Text, Source = "setup", RequestId = requestId };
    }

    /// <summary>
    /// What the helper's answer to an unlock means for a removal. A device that was unplugged ("pending": the helper
    /// would turn it back on when it returns) counts as not turned back on, because after the removal no helper is
    /// left to do that; the next restart does.
    /// </summary>
    internal static HelperUnlockOutcome UnlockOutcome(HelperResponse response)
    {
        if (!response.Ok)
        {
            Log.Error($"The helper could not unlock: {response.Error}");
            return HelperUnlockOutcome.Failed;
        }

        RestoreReport? report = response.Report;
        if (report is null)
        {
            return HelperUnlockOutcome.Unlocked;
        }

        Log.Info($"Unlock: {report.Summary}");
        List<RestoreLine> notBack = [.. report.Lines.Where(l => l.Outcome is RestoreOutcome.Failed or RestoreOutcome.Pending)];
        foreach (RestoreLine line in notBack)
        {
            Log.Warn($"Not turned back on: {line.DeviceName}: {line.Message}");
        }

        return notBack.Count == 0 ? HelperUnlockOutcome.Unlocked : HelperUnlockOutcome.Failed;
    }

    public void WriteUninstallEntry() =>
        UninstallEntry.For(HelperPaths.InstalledExe, AppVersion.Text, HelperFiles.TotalBytes(HelperPaths.InstallDirectory)).Write();

    public void DeleteUninstallEntry() => UninstallEntry.Delete();

    public bool RemoveFiles() => HelperFiles.RemoveInstallFolder(HelperPaths.InstallDirectory);

    public bool RemoveDataDirectory()
    {
        // The setup log is in that folder. The last lines go to this account's own BlindsDown log instead, so a
        // log line can't put a file back into the folder while it is being removed.
        string ownLog = AppPaths.ForCurrentUser().LogFile;
        Log.Info($"Removing {HelperPaths.DataDirectory}; this log continues in {ownLog}.");
        Log.Initialize(ownLog);
        return HelperFiles.RemoveTree(HelperPaths.DataDirectory);
    }
}
