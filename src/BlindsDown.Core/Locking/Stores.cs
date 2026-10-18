using System.Text;
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
    /// <summary>The entries; empty when there are none (or the file is damaged beyond reading). Throws
    /// <see cref="PendingRestoreUnreadableException"/> when the file exists but can't be opened right now: its
    /// entries are still owed, so it must not be overwritten.</summary>
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

/// <summary>The pending-restore file exists but can't be opened right now (another program holds it, or access is
/// denied). Unlike a damaged file, it still holds restores BlindsDown owes, so nothing may replace it.</summary>
public sealed class PendingRestoreUnreadableException : Exception
{
    public PendingRestoreUnreadableException()
    {
    }

    public PendingRestoreUnreadableException(string message)
        : base(message)
    {
    }

    public PendingRestoreUnreadableException(string message, Exception innerException)
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

        List<string?> copies = StoreFile.ReadCopies(path);
        foreach (string? text in copies)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

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

        throw new JournalCorruptException(copies.Contains(null)
            ? $"The lock journal at '{path}' exists but could not be opened (another program may be using it)."
            : $"The lock journal at '{path}' exists but could not be read.");
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
        bool blocked = false;
        foreach (string? text in StoreFile.ReadCopies(path))
        {
            if (text is null)
            {
                blocked = true;
                continue;
            }

            List<LockAction>? list = null;
            try
            {
                list = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize(text, CoreJsonContext.Default.ListLockAction);
            }
            catch (JsonException)
            {
                // Try the next copy.
            }

            if (list is null)
            {
                continue;
            }

            if (blocked)
            {
                break; // An older copy: the newer one that can't be opened may owe more.
            }

            list.RemoveAll(FileJournalStore.IsDamaged);
            return list;
        }

        if (blocked)
        {
            throw new PendingRestoreUnreadableException($"The list of microphones to restore at '{path}' could not be opened (another program may be using it).");
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

/// <summary>Reads the stores' files.</summary>
internal static class StoreFile
{
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Every copy of a file written by <see cref="AtomicFile"/> that exists, best first (the file, a ".tmp" left by a
    /// crash, the ".bak"): its text, or null for a copy that exists but can't be opened (another program holds it, or
    /// access is denied). That is usually brief, so such a copy is tried again a few times over a moment first.
    /// </summary>
    public static List<string?> ReadCopies(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            var copies = new List<string?>();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    copies.Add(File.ReadAllText(candidate, Encoding.UTF8));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    copies.Add(null);
                }
            }

            if (!copies.Contains(null) || attempt == Attempts)
            {
                return copies;
            }

            Thread.Sleep(RetryDelay);
        }
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
