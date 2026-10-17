using System.Text;
using System.Text.Json;
using BlindsDown.Core.Json;

namespace BlindsDown.Core.Usage;

/// <summary>
/// Local usage history: one JSON object per line in %LocalAppData%\BlindsDown\history.jsonl.
/// It never leaves this PC. Old entries are pruned according to the retention setting.
/// </summary>
public sealed class HistoryStore(string path)
{
    private readonly object _gate = new();

    public void Append(UsageRecord record)
    {
        string line = JsonSerializer.Serialize(record, CoreJsonLineContext.Default.UsageRecord);
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

    /// <summary>Reads all records, newest first. Damaged lines (e.g. from a power cut mid-write) are skipped.</summary>
    public List<UsageRecord> ReadAll()
    {
        var records = new List<UsageRecord>();
        lock (_gate)
        {
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
                    UsageRecord? record = JsonSerializer.Deserialize(line, CoreJsonLineContext.Default.UsageRecord);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        records.Sort((a, b) => b.Start.CompareTo(a.Start));
        return records;
    }

    /// <summary>Deletes entries that ended before <paramref name="cutoff"/>.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return;
            }

            List<UsageRecord> keep = ReadAll().Where(r => r.End >= cutoff).OrderBy(r => r.Start).ToList();
            var sb = new StringBuilder();
            foreach (UsageRecord record in keep)
            {
                sb.Append(JsonSerializer.Serialize(record, CoreJsonLineContext.Default.UsageRecord)).Append('\n');
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

    /// <summary>
    /// Joins records of the same app and device that are separated by short gaps (apps often
    /// re-open the mic for a moment), so "Zoom 14:02-14:47" shows as one line.
    /// </summary>
    public static List<UsageRecord> MergeShortGaps(IEnumerable<UsageRecord> records, TimeSpan maxGap)
    {
        var merged = new List<UsageRecord>();
        foreach (IGrouping<string, UsageRecord> group in records.GroupBy(r => $"{r.Kind}|{r.AppKey}|{r.DeviceName}", StringComparer.OrdinalIgnoreCase))
        {
            UsageRecord? current = null;
            foreach (UsageRecord r in group.OrderBy(r => r.Start))
            {
                if (current is not null && r.Start - current.End <= maxGap)
                {
                    current = current with { End = r.End > current.End ? r.End : current.End, Approximate = current.Approximate || r.Approximate };
                }
                else
                {
                    if (current is not null)
                    {
                        merged.Add(current);
                    }

                    current = r;
                }
            }

            if (current is not null)
            {
                merged.Add(current);
            }
        }

        merged.Sort((a, b) => b.Start.CompareTo(a.Start));
        return merged;
    }
}
