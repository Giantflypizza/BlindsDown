using System.Text;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Helper;
using BlindsDown.Core.IO;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;
using BlindsDown.Devices.Pnp;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown;

/// <summary>The one-shot command-line modes (no tray icon).</summary>
internal static class CommandLineTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>"--snapshot [file]": writes the state of every microphone, camera and microphone-only hardware
    /// device to a text file. Read-only.</summary>
    public static int WriteSnapshot(string? file, AppPaths paths)
    {
        if (!IsLocalTarget(file, "--snapshot"))
        {
            return 1;
        }

        try
        {
            using var engineThread = new EngineThread("BlindsDown snapshot", ex => Log.Error("Snapshot thread error", ex));
            string text = engineThread.Invoke(
                () =>
                {
                    using var backend = new CoreAudioMicBackend();
                    var snapshot = new AudioSnapshot(backend.EnumerateMics(), backend.GetDefaultMics(), SystemClock.Instance.UtcNow);
                    return SnapshotText.Format(snapshot) + Environment.NewLine + FormatPnpDevices();
                },
                Timeout);

            string path = file ?? Path.Combine(paths.DataDirectory, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text, Encoding.UTF8);
            return 0;
        }
        catch (Exception ex) when (ex is DeviceOperationException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            Log.Error("Snapshot failed", ex);
            return 1;
        }
    }

    /// <summary>
    /// "--selftest [file]": an end-to-end check against the REAL microphones. Takes a snapshot, locks,
    /// verifies every microphone in scope is muted, unlocks, and verifies the snapshot afterwards is
    /// identical to the one before. Mics are muted for about a second. Uses the normal journal, so if
    /// anything goes wrong mid-test, the app (or --restore) recovers exactly as after a crash.
    /// Writes a report (to <paramref name="file"/> or the data folder) and returns 0 on success.
    /// </summary>
    public static int SelfTest(string? file, AppPaths paths)
    {
        if (!IsLocalTarget(file, "--selftest"))
        {
            return 1; // Before muting anything: the report couldn't be written anyway.
        }

        var report = new StringBuilder();
        bool passed = false;
        try
        {
            var journalStore = new FileJournalStore(paths.JournalFile);
            if (journalStore.Load() is not null)
            {
                report.AppendLine("FAIL: a lock is active (or was interrupted). Unlock or run --restore first.");
            }
            else
            {
                AppSettings settings = new SettingsStore(paths.SettingsFile).Load();
                using var engineThread = new EngineThread("BlindsDown self-test", ex => Log.Error("Self-test thread error", ex));
                passed = engineThread.Invoke(() => RunSelfTest(paths, settings, journalStore, report), TimeSpan.FromSeconds(60));
            }
        }
        catch (Exception ex) when (ex is DeviceOperationException or JournalCorruptException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            report.AppendLine("FAIL: " + ex.Message);
            Log.Error("Self-test failed", ex);
        }

        report.AppendLine(passed ? "RESULT: PASS" : "RESULT: FAIL");
        string path = file ?? Path.Combine(paths.DataDirectory, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Writing the self-test report failed", ex);
        }

        Log.Info("Self-test " + (passed ? "passed" : "FAILED"));
        return passed ? 0 : 1;
    }

    private static bool RunSelfTest(AppPaths paths, AppSettings settings, IJournalStore journalStore, StringBuilder report)
    {
        using var backend = new CoreAudioMicBackend();
        var engine = new LockEngine(backend, journalStore, new FilePendingRestoreStore(paths.PendingRestoreFile), SystemClock.Instance, () => settings);
        engine.Initialize();

        AudioSnapshot before = Snapshot(backend);
        report.AppendLine("=== BEFORE ===").AppendLine(SnapshotText.Format(before));

        bool ok = true;
        RestoreReport? restore;
        LockStatusInfo status = engine.Lock();
        try
        {
            AudioSnapshot during = Snapshot(backend);
            report.AppendLine($"=== LOCKED: {status.State} ===").AppendLine(SnapshotText.Format(during));
            foreach (MicEndpoint mic in during.Mics.Where(m => m.IsUsable && !settings.IsExcluded(m, during.Mics)))
            {
                if (mic.Muted != true)
                {
                    ok = false;
                    report.AppendLine($"FAIL: \"{mic.Name}\" was NOT muted while locked.");
                }
            }

            if (status.State != LockState.Locked)
            {
                ok = false;
                report.AppendLine($"FAIL: lock state was {status.State}, expected Locked. Problems: {string.Join("; ", status.Problems)}");
            }
        }
        finally
        {
            // Whatever happened above, never leave the microphones muted.
            restore = engine.Unlock();
        }

        report.AppendLine("=== RESTORE REPORT ===").AppendLine(restore?.Summary ?? "(none)");
        foreach (RestoreLine line in restore?.Lines ?? [])
        {
            report.AppendLine($"  {line.Outcome}: {line.DeviceName}: {line.Message}");
        }

        AudioSnapshot after = Snapshot(backend);
        report.AppendLine("=== AFTER ===").AppendLine(SnapshotText.Format(after));

        string beforeText = SnapshotText.Format(before, includeTimestamp: false);
        string afterText = SnapshotText.Format(after, includeTimestamp: false);
        if (beforeText != afterText)
        {
            ok = false;
            report.AppendLine("FAIL: device state after unlock differs from before the lock:");
            string[] beforeLines = beforeText.Split('\n');
            string[] afterLines = afterText.Split('\n');
            foreach (string line in beforeLines.Except(afterLines))
            {
                report.AppendLine("  before: " + line.TrimEnd());
            }

            foreach (string line in afterLines.Except(beforeLines))
            {
                report.AppendLine("  after:  " + line.TrimEnd());
            }
        }
        else
        {
            report.AppendLine("OK: every microphone and default device is exactly as before.");
        }

        return ok;
    }

    /// <summary>Cameras and microphone-only hardware, as the helper would see them (listing needs no admin rights).</summary>
    private static string FormatPnpDevices()
    {
        var text = new StringBuilder("Cameras and microphone hardware (present | state | kind | type | name | id):").AppendLine();
        try
        {
            IReadOnlyList<PnpDevice> devices = new CfgMgrPnpBackend().Enumerate();
            if (devices.Count == 0)
            {
                text.AppendLine("  (none)");
            }

            foreach (PnpDevice device in devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                string state = device.IsDisabled ? "disabled" : device.IsStarted ? "running" : $"problem {device.ProblemCode}";
                string type = device.Kind == DeviceKind.Camera ? (device.CameraType?.ToString() ?? "Color") : device.IsVirtual ? "virtual" : "physical";
                text.AppendLine($"  {(device.IsPresent ? "present" : "absent "),-7} | {state,-10} | {device.Kind,-18} | {type,-16} | {device.Name} | {device.InstanceId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing cameras for the snapshot failed", ex);
            text.AppendLine("  (could not be listed: " + ex.Message + ")");
        }

        return text.ToString();
    }

    /// <summary>BlindsDown never opens network paths (Windows would connect to the share on its behalf), so a report file
    /// must be on a local drive. No file name means BlindsDown's own data folder, which always is.</summary>
    private static bool IsLocalTarget(string? file, string option)
    {
        if (file is null)
        {
            return true;
        }

        try
        {
            if (Usage.ImagePaths.IsLocalDrivePath(Path.GetFullPath(file)))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            Log.Error($"{option}: invalid file name", ex);
        }

        Log.Warn($"{option}: refused to write to a path that isn't on a local drive.");
        MessageBox.Show(
            $"{option} only writes to a local drive. Choose a file on this PC (for example in your Documents folder).",
            "BlindsDown",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }

    private static AudioSnapshot Snapshot(CoreAudioMicBackend backend) =>
        new(backend.EnumerateMics(), backend.GetDefaultMics(), SystemClock.Instance.UtcNow);

    /// <summary>
    /// "--restore": undoes everything recorded in the lock journal (and pending restores), without
    /// starting the tray app, and asks the camera helper (if installed) to turn its devices back on first.
    /// For when something went wrong and the user just wants their devices back.
    /// </summary>
    public static int EmergencyRestore(AppPaths paths)
    {
        string? helperMessage = null;
        try
        {
            AppSettings settings = new SettingsStore(paths.SettingsFile).Load();
            using var engineThread = new EngineThread("BlindsDown restore", ex => Log.Error("Restore thread error", ex));
            CoreAudioMicBackend? backend = null;
            LockEngine? engine = null;
            var finished = new List<string>();
            try
            {
                // 1. Read the microphone lock while the helper's devices are still off: a default microphone that Windows
                //    moved when the helper turned hardware off is recorded now, so it can be put back below.
                bool corrupt = engineThread.Invoke(
                    () =>
                    {
                        backend = new CoreAudioMicBackend();
                        engine = new LockEngine(
                            backend,
                            new FileJournalStore(paths.JournalFile),
                            new FilePendingRestoreStore(paths.PendingRestoreFile),
                            SystemClock.Instance,
                            () => settings);
                        CollectFinishedRestores(engine, finished);
                        engine.Initialize(); // Also restores devices left over from earlier locks that are back now.
                        engine.NoteDefaultsSettled();
                        return engine.JournalWasCorrupt;
                    },
                    Timeout);

                // 2. The helper's part (cameras and microphone hardware), first, so the hardware is back before its
                //    microphones are restored. Only what it really turns back on is waited for: a device the user disabled
                //    stays off, and its microphones never come back.
                HashSet<string> offBefore = DevicesTurnedOff();
                helperMessage = UnlockHelper(paths, out bool helperUnlocked);
                HashSet<string> turnedOn = helperUnlocked ? offBefore.Except(DevicesTurnedOff(), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

                if (corrupt)
                {
                    string damaged = "BlindsDown's lock journal was damaged, so it can't tell which microphones it muted or turned off.\n\n"
                        + "Open Windows Settings > System > Sound and check that your microphones are on and not muted.";
                    Show(helperMessage, damaged);
                    return 0;
                }

                // 3. The microphones.
                bool helperHadSomething = helperMessage is not null;
                string message = engineThread.Invoke(
                    () => RestoreMicrophones(engine!, backend!, turnedOn, () => File.Exists(paths.PendingRestoreFile), helperHadSomething, finished),
                    Timeout + MicrophoneReturn.Patience + MicrophoneReturn.Patience);

                Show(helperMessage, message);
                return 0;
            }
            finally
            {
                engineThread.Invoke(
                    () =>
                    {
                        backend?.Dispose();
                        return true;
                    },
                    Timeout);
            }
        }
        catch (Exception ex) when (ex is DeviceOperationException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            Log.Error("Emergency restore failed", ex);
            MessageBox.Show(
                (helperMessage is null ? string.Empty : helperMessage + "\n\n")
                + "BlindsDown could not restore your microphones automatically: " + ex.Message
                + "\n\nOpen Windows Settings > System > Sound and check that your microphones are on and not muted.",
                "BlindsDown – restore",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }
    }

    /// <summary>Notes, as the engine words them, every restore an earlier unlock owed that <paramref name="engine"/>
    /// finishes from now on (a microphone that is back), so the --restore message can list them.</summary>
    internal static void CollectFinishedRestores(LockEngine engine, List<string> finished) =>
        engine.AlertRaised += alert =>
        {
            if (alert.Kind == EngineAlertKind.DeferredRestoreDone)
            {
                finished.Add(alert.Message);
            }
        };

    /// <summary>
    /// Step 3 of --restore, on the engine thread: unlocks the microphones, finishes the restores still owed to those on the
    /// hardware the helper just turned back on (<paramref name="turnedOn"/>), and says what is true then. Such a restore
    /// can be owed by this unlock or by an earlier one. With no lock left, Unlock does nothing and the engine's start ran
    /// while that hardware was still off, so only the loop here finishes it. <paramref name="stillOwed"/> tells whether
    /// any restore is still owed (the pending-restore file exists); <paramref name="finished"/> is what
    /// <see cref="CollectFinishedRestores"/> noted.
    /// </summary>
    internal static string RestoreMicrophones(
        LockEngine engine,
        IMicBackend backend,
        IReadOnlySet<string> turnedOn,
        Func<bool> stillOwed,
        bool helperHadSomething,
        IReadOnlyList<string> finished)
    {
        if (turnedOn.Count > 0)
        {
            // Windows needs a moment to list those microphones again; restoring them before that would
            // find them "not connected" and leave them muted (and turned off) until BlindsDown runs again.
            MicrophoneReturn.WaitFor(backend, turnedOn);
        }

        RestoreReport? report = engine.Unlock();
        if (turnedOn.Count > 0 && (report is { PendingCount: > 0 } || stillOwed()))
        {
            // Microphones that came back just now (some slowly), owed a restore by this unlock or an earlier one, whatever
            // the report says: let the engine finish them before this process ends.
            DateTime deadline = DateTime.UtcNow + MicrophoneReturn.Patience;
            while (true)
            {
                engine.Reconcile();
                if (!stillOwed() || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Thread.Sleep(250);
            }
        }

        return Describe(report, helperHadSomething, finished, stillOwed());
    }

    /// <summary>The microphones' part of the --restore message, from what is true when it is shown. Without a lock
    /// (<paramref name="report"/> is null), restores an earlier lock owed can still have been finished now, or still be owed.</summary>
    internal static string Describe(RestoreReport? report, bool helperHadSomething, IReadOnlyList<string> finished, bool stillOwed)
    {
        if (report is null && finished.Count == 0 && !stillOwed)
        {
            return helperHadSomething
                ? "Microphones: nothing to restore (they weren't locked)."
                : "Nothing to restore: BlindsDown has no active lock.";
        }

        var text = new StringBuilder(helperHadSomething ? "Microphones: " : string.Empty);
        if (report is null)
        {
            text.AppendLine(helperHadSomething
                ? "they weren't locked, but an earlier lock still owed some of them a restore."
                : "BlindsDown has no active lock, but an earlier lock still owed some microphones a restore.");
        }
        else
        {
            text.Append(report.Summary).AppendLine().AppendLine();
            foreach (RestoreLine line in report.Lines)
            {
                text.Append("• ").Append(line.DeviceName).Append(": ").AppendLine(line.Message);
            }
        }

        if (finished.Count > 0)
        {
            text.AppendLine().AppendLine(report is null ? "Restored now:" : "Since then:");
            foreach (string note in finished)
            {
                text.Append("• ").AppendLine(note);
            }
        }

        if (stillOwed)
        {
            text.AppendLine().Append("Start BlindsDown once to finish restoring a microphone that wasn't back yet.");
        }

        return text.ToString();
    }

    private static void Show(string? helperMessage, string message)
    {
        if (helperMessage is not null)
        {
            message = helperMessage + Environment.NewLine + Environment.NewLine + message;
        }

        Log.Info("Emergency restore: " + message.ReplaceLineEndings(" "));
        MessageBox.Show(message, "BlindsDown – restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>What to say when the helper couldn't unlock. A restart does NOT help while it is installed: it keeps its
    /// lock record and turns the devices off again when Windows starts.</summary>
    private const string HelperStillLocked =
        "The camera helper keeps your cameras off until it unlocks them (a restart locks them again). Run --restore again in a moment, use Repair in BlindsDown's Settings > Camera, or remove the camera helper (Settings > Camera, or Apps > Installed apps); once it is removed, restarting the PC turns every camera back on.";

    /// <summary>Cameras and microphone hardware that are off right now (read-only; empty when nothing is off or the list
    /// can't be read). A camera can also be a microphone's hardware, so every kind counts.</summary>
    private static HashSet<string> DevicesTurnedOff()
    {
        try
        {
            return new CfgMgrPnpBackend().Enumerate()
                .Where(d => d.IsPresent && d.IsDisabled)
                .Select(d => d.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Emergency restore: listing devices failed", ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>What to say when Windows couldn't tell whether the helper is installed, and the helper couldn't be reached
    /// either: there may be no helper at all, or one that still has cameras off. Followed by <see cref="HelperStillLocked"/>.</summary>
    private const string CamerasNotChecked =
        "BlindsDown couldn't check your cameras: Windows didn't say whether the camera helper is installed, and the helper couldn't be reached. If you don't use the camera helper, you can ignore this. ";

    /// <summary>The helper's part of --restore, with the real helper. The request goes off the UI thread (the pipe I/O is
    /// asynchronous), with a timeout longer than the helper's own two-minute queue limit, so its answer always arrives.</summary>
    private static string? UnlockHelper(AppPaths paths, out bool unlocked) =>
        UnlockHelper(
            paths.HelperStateFile,
            HelperSetup.Query,
            request => Task.Run(() => new HelperClient().SendAsync(request, HelperTimeouts.ChangeTimeout)).GetAwaiter().GetResult(),
            out unlocked);

    /// <summary>The helper's part of --restore. Returns what to tell the user, or null when there is no helper or it had
    /// nothing to undo. <paramref name="unlocked"/> is true when the helper turned its devices back on. When Windows
    /// couldn't be asked whether the helper is installed (<see cref="HelperInstallInfo.QueryFailed"/>), the helper is
    /// asked all the same: that failure says nothing about it.</summary>
    internal static string? UnlockHelper(string helperStateFile, Func<HelperInstallInfo> query, Func<HelperRequest, HelperResponse> send, out bool unlocked)
    {
        unlocked = false;
        bool queryFailed = false;
        try
        {
            HelperInstallInfo info = query();
            if (!info.Installed && !info.QueryFailed)
            {
                return null; // Windows says there is no helper: nothing to undo, and nothing to say.
            }

            queryFailed = info.QueryFailed;

            // This user's own request: the tray app must not take it for another program's unlock.
            string requestId = HelperLink.NewRequestId();
            HelperLink.NoteOwnRequest(helperStateFile, requestId);
            var request = new HelperRequest { Command = HelperCommand.Unlock, Source = "--restore", ClientVersion = AppVersion.Text, RequestId = requestId };
            HelperResponse response = send(request);
            if (!response.Ok)
            {
                return "The camera helper didn't unlock: " + (response.Error?.TrimEnd('.') ?? "unknown error") + ". " + HelperStillLocked;
            }

            unlocked = true;
            if (response.Report is not RestoreReport report)
            {
                return null;
            }

            var text = new StringBuilder("Cameras: " + report.Summary).AppendLine();
            foreach (RestoreLine line in report.Lines)
            {
                text.Append("• ").Append(line.DeviceName).Append(": ").AppendLine(line.Message);
            }

            return text.ToString().TrimEnd();
        }
        catch (HelperUnavailableException ex) when (queryFailed)
        {
            // Most likely there is no helper, but Windows couldn't confirm it: the cameras weren't checked, so say so.
            Log.Warn("Emergency restore: Windows couldn't say whether the camera helper is installed, and it isn't answering: " + ex.Message);
            return CamerasNotChecked + HelperStillLocked;
        }
        catch (HelperUnavailableException ex)
        {
            Log.Error("Emergency restore: the camera helper isn't answering", ex);
            return "The camera helper isn't answering, so BlindsDown couldn't ask it to turn cameras back on. " + HelperStillLocked;
        }
        catch (Exception ex)
        {
            Log.Error("Emergency restore: asking the camera helper failed", ex);
            return (queryFailed ? CamerasNotChecked : "BlindsDown couldn't reach the camera helper. ") + HelperStillLocked;
        }
    }
}
