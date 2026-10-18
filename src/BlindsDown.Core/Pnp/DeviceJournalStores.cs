using System.Text.Json;
using System.Text.Json.Serialization;
using BlindsDown.Core.IO;
using BlindsDown.Core.Json;
using BlindsDown.Core.Locking;

namespace BlindsDown.Core.Pnp;

/// <summary>
/// File-backed journal (atomic writes, see <see cref="AtomicFile"/>). Every save is flushed to disk before the
/// device change it describes. Reading falls back to the ".tmp" and ".bak" copies a crash can leave. The owed
/// restores (see <see cref="OwedDevice"/>) live in their own file next to the journal, because an unlock deletes
/// the journal.
/// </summary>
public sealed class FileDeviceJournalStore : IDeviceJournalStore
{
    /// <summary>The owed restores' file name, in the journal's folder unless given.</summary>
    public const string OwedFileName = "device-owed-restores.json";

    private readonly string _path;
    private readonly string _owedPath;

    /// <param name="path">The journal file.</param>
    /// <param name="owedPath">The owed restores' file; by default <see cref="OwedFileName"/> next to the journal, so it
    /// gets the same protected folder.</param>
    public FileDeviceJournalStore(string path, string? owedPath = null)
    {
        _path = path;
        _owedPath = owedPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, OwedFileName);
    }

    public DeviceLockJournal? Load()
    {
        if (!AtomicFile.AnyCopyExists(_path))
        {
            return null;
        }

        foreach (string text in AtomicFile.ReadCandidates(_path))
        {
            try
            {
                DeviceLockJournal? journal = JsonSerializer.Deserialize(text, DeviceJournalJsonContext.Default.DeviceLockJournal);
                if (journal is not null)
                {
                    journal.Repair();
                    return journal;
                }
            }
            catch (JsonException)
            {
                // Try the next copy.
            }
        }

        if (!File.Exists(_path) && !File.Exists(_path + ".bak"))
        {
            // Only a half-written ".tmp": the helper died during the very first save of a lock. That save never
            // returned, so no device was changed yet and there is nothing to recover.
            return null;
        }

        throw new JournalCorruptException($"The device lock journal at '{_path}' exists but could not be read.");
    }

    public void Save(DeviceLockJournal journal) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(journal, DeviceJournalJsonContext.Default.DeviceLockJournal));

    public void Delete() => AtomicFile.Delete(_path);

    public IReadOnlyList<OwedDevice> LoadOwedRestores()
    {
        foreach (string text in AtomicFile.ReadCandidates(_owedPath))
        {
            try
            {
                if (JsonSerializer.Deserialize(text, OwedDeviceJsonContext.Default.ListOwedDevice) is { } owed)
                {
                    return OwedDevice.Repair(owed);
                }
            }
            catch (JsonException)
            {
                // Try the next copy.
            }
        }

        return [];
    }

    public void SaveOwedRestores(IReadOnlyList<OwedDevice> owed)
    {
        if (owed.Count == 0)
        {
            AtomicFile.Delete(_owedPath);
            return;
        }

        AtomicFile.WriteAllText(_owedPath, JsonSerializer.Serialize([.. owed], OwedDeviceJsonContext.Default.ListOwedDevice));
    }

    public override string ToString() => _path;
}

/// <summary>In-memory journal for tests (round-trips through JSON like the real one).</summary>
public sealed class MemoryDeviceJournalStore : IDeviceJournalStore
{
    private string? _json;
    private string? _owedJson;

    public int SaveCount { get; private set; }

    /// <summary>Makes <see cref="Load"/> fail like a journal file that exists but can't be read.</summary>
    public bool Corrupt { get; set; }

    public DeviceLockJournal? Load()
    {
        if (Corrupt)
        {
            throw new JournalCorruptException("Simulated corruption.");
        }

        if (_json is null)
        {
            return null;
        }

        DeviceLockJournal? journal = JsonSerializer.Deserialize(_json, DeviceJournalJsonContext.Default.DeviceLockJournal);
        journal?.Repair();
        return journal;
    }

    public void Save(DeviceLockJournal journal)
    {
        _json = JsonSerializer.Serialize(journal, DeviceJournalJsonContext.Default.DeviceLockJournal);
        SaveCount++;
    }

    public void Delete() => _json = null;

    public IReadOnlyList<OwedDevice> LoadOwedRestores() =>
        _owedJson is null ? [] : OwedDevice.Repair(JsonSerializer.Deserialize(_owedJson, OwedDeviceJsonContext.Default.ListOwedDevice));

    public void SaveOwedRestores(IReadOnlyList<OwedDevice> owed) =>
        _owedJson = owed.Count == 0 ? null : JsonSerializer.Serialize([.. owed], OwedDeviceJsonContext.Default.ListOwedDevice);
}

/// <summary>The owed restores' file (%ProgramData%\BlindsDown\device-owed-restores.json), written like the journal.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(List<OwedDevice>))]
internal sealed partial class OwedDeviceJsonContext : JsonSerializerContext
{
}
