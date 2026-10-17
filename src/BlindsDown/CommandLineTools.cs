using System.Text;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Core.IO;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;
using BlindsDown.Infrastructure;

namespace BlindsDown;

/// <summary>The one-shot command-line modes (no tray icon).</summary>
internal static class CommandLineTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>"--snapshot [file]": writes every microphone's state to a text file. Read-only.</summary>
    public static int WriteSnapshot(string? file, AppPaths paths)
    {
        try
        {
            using var engineThread = new EngineThread("BlindsDown snapshot", ex => Log.Error("Snapshot thread error", ex));
            string text = engineThread.Invoke(
                () =>
                {
                    using var backend = new CoreAudioMicBackend();
                    var snapshot = new AudioSnapshot(backend.EnumerateMics(), backend.GetDefaultMics(), SystemClock.Instance.UtcNow);
                    return SnapshotText.Format(snapshot);
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

    private static AudioSnapshot Snapshot(CoreAudioMicBackend backend) =>
        new(backend.EnumerateMics(), backend.GetDefaultMics(), SystemClock.Instance.UtcNow);

    /// <summary>
    /// "--restore": undoes everything recorded in the lock journal (and pending restores), without
    /// starting the tray app. For when something went wrong and the user just wants their devices back.
    /// </summary>
    public static int EmergencyRestore(AppPaths paths)
    {
        try
        {
            AppSettings settings = new SettingsStore(paths.SettingsFile).Load();
            using var engineThread = new EngineThread("BlindsDown restore", ex => Log.Error("Restore thread error", ex));
            string message = engineThread.Invoke(
                () =>
                {
                    using var backend = new CoreAudioMicBackend();
                    var engine = new LockEngine(
                        backend,
                        new FileJournalStore(paths.JournalFile),
                        new FilePendingRestoreStore(paths.PendingRestoreFile),
                        SystemClock.Instance,
                        () => settings);
                    engine.Initialize(); // Also restores devices left over from earlier locks that are back now.
                    if (engine.JournalWasCorrupt)
                    {
                        return "BlindsDown's lock journal was damaged, so it can't tell which microphones it muted.\n\n"
                            + "Open Windows Settings > System > Sound and check that your microphones are not muted.";
                    }

                    RestoreReport? report = engine.Unlock();
                    if (report is null)
                    {
                        return "Nothing to restore: BlindsDown has no active lock.";
                    }

                    var sb = new StringBuilder(report.Summary).AppendLine().AppendLine();
                    foreach (RestoreLine line in report.Lines)
                    {
                        sb.Append("• ").Append(line.DeviceName).Append(": ").AppendLine(line.Message);
                    }

                    return sb.ToString();
                },
                Timeout);

            Log.Info("Emergency restore: " + message.ReplaceLineEndings(" "));
            MessageBox.Show(message, "BlindsDown – restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex) when (ex is DeviceOperationException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            Log.Error("Emergency restore failed", ex);
            MessageBox.Show(
                "BlindsDown could not restore your devices automatically: " + ex.Message
                + "\n\nOpen Windows Settings > System > Sound and check that your microphones are not muted.",
                "BlindsDown – restore",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }
    }
}
