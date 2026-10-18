using System.Text;
using System.Text.Json;
using BlindsDown.Core.Json;

namespace BlindsDown.Core.Usage;

public enum LockEventAction
{
    Lock,
    Unlock,

    /// <summary>The lock goes on, but what it covers changed (another program changed the camera lock, or the
    /// cameras were locked again): replaces the open lock's summary.</summary>
    Update,
}

/// <summary>Who or what triggered a lock or unlock.</summary>
public enum LockEventSource
{
    /// <summary>The hotkey.</summary>
    Hotkey,

    /// <summary>A click on the tray icon or its menu.</summary>
    Tray,

    /// <summary>A command line (--lock/--unlock/--toggle/--restore) or another program.</summary>
    OtherProgram,

    /// <summary>Automatically, when Windows was locked or the PC went to sleep.</summary>
    Automatic,

    /// <summary>The crash-recovery dialog at startup.</summary>
    Recovery,

    /// <summary>"Unlock and exit".</summary>
    Exit,
}

/// <summary>One lock or unlock, for the Activity timeline (lock-events.jsonl, one JSON object per line).</summary>
public sealed record LockEventRecord
{
    public required DateTimeOffset At { get; init; }

    public required LockEventAction Action { get; init; }

    public required LockEventSource Source { get; init; }

    /// <summary>E.g. "4 microphones, 1 camera" or the restore report summary.</summary>
    public string? Summary { get; init; }

    /// <summary>True when the lock was only partial or the restore was not exact.</summary>
    public bool HadProblems { get; init; }
}

/// <summary>
/// Local log of locks and unlocks (%LocalAppData%\BlindsDown\lock-events.jsonl), same rules as
/// <see cref="HistoryStore"/>: append-only JSON lines, damaged lines skipped, pruned with the history retention,
/// cleared with "Clear history". It never leaves this PC. Thread-safe: the tray and the engine thread both
/// write to it.
/// </summary>
public sealed class LockEventLog(string path)
{
    private readonly object _gate = new();

    public void Append(LockEventRecord record)
    {
        string line = JsonSerializer.Serialize(record, CoreJsonLineContext.Default.LockEventRecord);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            // A power cut mid-write can leave a last line without its newline; appending straight after
            // it would glue this record onto the damaged line and lose it too.
            bool startNewLine = false;
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                startNewLine = stream.ReadByte() != '\n';
            }

            stream.Seek(0, SeekOrigin.End);
            byte[] bytes = Encoding.UTF8.GetBytes((startNewLine ? "\n" : string.Empty) + line + "\n");
            stream.Write(bytes);
        }
    }

    /// <summary>All records, newest first. Damaged lines (e.g. from a power cut mid-write) are skipped. Records with
    /// the same time keep the order they were written in (the later one first), so a lock and its immediate
    /// unlock never swap.</summary>
    public List<LockEventRecord> ReadAll()
    {
        List<LockEventRecord> records;
        lock (_gate)
        {
            records = ReadInFileOrder();
        }

        records.Reverse();
        return [.. records.OrderByDescending(r => r.At)];
    }

    /// <summary>Deletes records from before <paramref name="cutoff"/>.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return;
            }

            // Oldest first on disk, so appends keep the file in order (and damaged lines are dropped).
            var sb = new StringBuilder();
            foreach (LockEventRecord record in ReadInFileOrder().Where(r => r.At >= cutoff).OrderBy(r => r.At))
            {
                sb.Append(JsonSerializer.Serialize(record, CoreJsonLineContext.Default.LockEventRecord)).Append('\n');
            }

            IO.AtomicFile.WriteAllText(path, sb.ToString());
            File.Delete(path + ".bak");
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            IO.AtomicFile.Delete(path);
        }
    }

    public override string ToString() => path;

    /// <summary>Call with <see cref="_gate"/> held.</summary>
    private List<LockEventRecord> ReadInFileOrder()
    {
        var records = new List<LockEventRecord>();
        if (!File.Exists(path))
        {
            return records;
        }

        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                LockEventRecord? record = JsonSerializer.Deserialize(line, CoreJsonLineContext.Default.LockEventRecord);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }
}
