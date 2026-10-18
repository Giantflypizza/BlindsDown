using BlindsDown.Core.Locking;

namespace BlindsDown.Tests.Core.Fakes;

/// <summary>
/// Simulates the process being killed at an exact point. Every journal/pending-store write and every
/// device mutation passes two crash points: right before it happens and right after it happened.
/// When armed with N, the N-th point throws <see cref="SimulatedCrashException"/>, which nothing in
/// Core catches, so the engine stops dead exactly like a killed process. The test then builds a new
/// engine over the persisted stores and the same devices.
/// </summary>
internal sealed class CrashPlan
{
    private int _crashAt;
    private bool _counting;

    /// <summary>Crash points passed since <see cref="ArmAt"/> or <see cref="Record"/>.</summary>
    public List<string> Seen { get; } = [];

    /// <summary>Description of the point that crashed, for failure messages.</summary>
    public string? LastCrash { get; private set; }

    /// <summary>Runs at every point, armed or not: lets another app act at an exact moment, e.g. unmute a
    /// microphone right before BlindsDown turns it off.</summary>
    public Action<string>? Interject { get; set; }

    /// <summary>Crash at the <paramref name="point"/>-th point from now (1-based).</summary>
    public void ArmAt(int point)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(point, 1);
        _crashAt = point;
        _counting = true;
        Seen.Clear();
        LastCrash = null;
    }

    /// <summary>Only records the points passed, so a test can find a point by name.</summary>
    public void Record()
    {
        _crashAt = 0;
        _counting = true;
        Seen.Clear();
    }

    public void Disarm()
    {
        _crashAt = 0;
        _counting = false;
    }

    public void Point(string where)
    {
        Interject?.Invoke(where);
        if (!_counting)
        {
            return;
        }

        Seen.Add(where);
        if (Seen.Count == _crashAt)
        {
            Disarm();
            LastCrash = where;
            throw new SimulatedCrashException(where);
        }
    }

    /// <summary>
    /// Builds a world with <paramref name="setup"/>, records the crash points <paramref name="operation"/>
    /// passes, and returns the 1-based index of the first one starting with <paramref name="where"/>.
    /// </summary>
    public static int Find(Func<LockWorld> setup, Action<LockWorld> operation, string where)
    {
        LockWorld probe = setup();
        probe.Crash.Record();
        operation(probe);
        probe.Crash.Disarm();
        int index = probe.Crash.Seen.FindIndex(p => p.StartsWith(where, StringComparison.Ordinal));
        return index >= 0
            ? index + 1
            : throw new InvalidOperationException($"No crash point '{where}' in: {string.Join(" | ", probe.Crash.Seen)}");
    }
}

/// <summary>Stands for "the process was killed here".</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Journal store that can be "killed" before or after any write. The inner store is what survives the crash.</summary>
internal sealed class CrashingJournalStore(IJournalStore inner, CrashPlan crash) : IJournalStore
{
    public int Saves { get; private set; }

    public LockJournal? Load() => inner.Load();

    public void Save(LockJournal journal)
    {
        crash.Point($"before journal save #{Saves + 1} ({journal.Phase})");
        inner.Save(journal);
        Saves++;
        crash.Point($"after journal save #{Saves} ({journal.Phase})");
    }

    public void Delete()
    {
        crash.Point("before journal delete");
        inner.Delete();
        crash.Point("after journal delete");
    }
}

/// <summary>Pending-restore store that can be "killed" before or after any write.</summary>
internal sealed class CrashingPendingRestoreStore(IPendingRestoreStore inner, CrashPlan crash) : IPendingRestoreStore
{
    public List<LockAction> Load() => inner.Load();

    public void Save(IReadOnlyCollection<LockAction> actions)
    {
        crash.Point($"before pending save ({actions.Count})");
        inner.Save(actions);
        crash.Point($"after pending save ({actions.Count})");
    }
}

/// <summary>A journal store whose writes fail like a full or read-only disk.</summary>
internal sealed class FailingJournalStore : IJournalStore
{
    public bool FailSaves { get; set; } = true;

    public LockJournal? Stored { get; private set; }

    public LockJournal? Load() => Stored;

    public void Save(LockJournal journal)
    {
        if (FailSaves)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        Stored = journal;
    }

    public void Delete() => Stored = null;
}

/// <summary>A pending store whose reads or writes fail transiently (e.g. another program holds the file, or the disk
/// is full), like <see cref="FilePendingRestoreStore"/> then.</summary>
internal sealed class FlakyPendingRestoreStore(IPendingRestoreStore inner) : IPendingRestoreStore
{
    public bool FailLoads { get; set; }

    public bool FailSaves { get; set; }

    public List<LockAction> Load() =>
        FailLoads ? throw new PendingRestoreUnreadableException("Held by another program (simulated).") : inner.Load();

    public void Save(IReadOnlyCollection<LockAction> actions)
    {
        if (FailSaves)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        inner.Save(actions);
    }
}
