using System.Text.Json;
using BlindsDown.Core.IO;
using BlindsDown.Core.Json;

namespace BlindsDown.Core.Locking;

/// <summary>Persists the lock journal. Every save must be durable before the device change it describes.</summary>
public interface IJournalStore
{
    /// <summary>Returns null when there is no journal. Throws <see cref="JournalCorruptException"/> when
    /// journal files exist but none of them can be read.</summary>
    LockJournal? Load();

    void Save(LockJournal journal);

    void Delete();
}

/// <summary>Devices that were unplugged at unlock and still need to be restored when they come back.</summary>
public interface IPendingRestoreStore
{
    List<LockAction> Load();

    void Save(IReadOnlyCollection<LockAction> actions);
}

public sealed class JournalCorruptException : Exception
{
    public JournalCorruptException()
    {
    }

    public JournalCorruptException(string message)
        : base(message)
    {
    }

    public JournalCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FileJournalStore(string path) : IJournalStore
{
    public LockJournal? Load()
    {
        if (!AtomicFile.AnyCopyExists(path))
        {
            return null;
        }

        foreach (string text in AtomicFile.ReadCandidates(path))
        {
            try
            {
                LockJournal? journal = JsonSerializer.Deserialize(text, CoreJsonContext.Default.LockJournal);
                if (journal is not null)
                {
                    journal.Actions ??= [];
                    journal.Actions.RemoveAll(IsDamaged);
                    return journal;
                }
            }
            catch (JsonException)
            {
                // Try the next copy.
            }
        }

        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            // Only a half-written ".tmp": the process died during the very first save of a lock. That save
            // never returned, so no device was changed yet and there is nothing to recover.
            return null;
        }

        throw new JournalCorruptException($"The lock journal at '{path}' exists but could not be read.");
    }

    public void Save(LockJournal journal) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(journal, CoreJsonContext.Default.LockJournal));

    public void Delete() => AtomicFile.Delete(path);

    /// <summary>
    /// A null entry or one without a device (only a damaged or hand-edited file has them) can't be undone
    /// and would reach the audio API with a null endpoint ID, so it is dropped.
    /// </summary>
    internal static bool IsDamaged(LockAction? action) => action is null || string.IsNullOrEmpty(action.TargetId);
}

public sealed class FilePendingRestoreStore(string path) : IPendingRestoreStore
{
    public List<LockAction> Load()
    {
        foreach (string text in AtomicFile.ReadCandidates(path))
        {
            try
            {
                List<LockAction>? list = JsonSerializer.Deserialize(text, CoreJsonContext.Default.ListLockAction);
                if (list is not null)
                {
                    list.RemoveAll(FileJournalStore.IsDamaged);
                    return list;
                }
            }
            catch (JsonException)
            {
            }
        }

        return [];
    }

    public void Save(IReadOnlyCollection<LockAction> actions)
    {
        if (actions.Count == 0)
        {
            AtomicFile.Delete(path);
            return;
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(actions.ToList(), CoreJsonContext.Default.ListLockAction));
    }
}

/// <summary>In-memory stores for tests and for the "--restore" dry runs.</summary>
public sealed class MemoryJournalStore : IJournalStore
{
    private string? _json;

    public int SaveCount { get; private set; }

    public bool Corrupt { get; set; }

    public LockJournal? Load()
    {
        if (Corrupt)
        {
            throw new JournalCorruptException("Simulated corruption.");
        }

        return _json is null ? null : JsonSerializer.Deserialize(_json, CoreJsonContext.Default.LockJournal);
    }

    public void Save(LockJournal journal)
    {
        _json = JsonSerializer.Serialize(journal, CoreJsonContext.Default.LockJournal);
        SaveCount++;
    }

    public void Delete() => _json = null;
}

public sealed class MemoryPendingRestoreStore : IPendingRestoreStore
{
    private string _json = "[]";

    public List<LockAction> Load() =>
        JsonSerializer.Deserialize(_json, CoreJsonContext.Default.ListLockAction) ?? [];

    public void Save(IReadOnlyCollection<LockAction> actions) =>
        _json = JsonSerializer.Serialize(actions.ToList(), CoreJsonContext.Default.ListLockAction);
}
