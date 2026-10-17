using System.Text;

namespace BlindsDown.Core.IO;

/// <summary>
/// Crash-safe file writes. A write goes to "file.tmp", is flushed to disk, and only then replaces
/// "file" (the previous version is kept as "file.bak"). A crash at any point leaves at least one
/// complete copy, and <see cref="ReadCandidates"/> returns them newest first.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tmp = path + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// The contents of every copy that exists, best first: the file itself, then a fully written
    /// ".tmp" left by a crash during replace, then the ".bak" (one version older).
    /// </summary>
    public static IEnumerable<string> ReadCandidates(string path)
    {
        foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
        {
            string? text = null;
            try
            {
                if (File.Exists(candidate))
                {
                    text = File.ReadAllText(candidate, Encoding.UTF8);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    public static bool AnyCopyExists(string path) =>
        File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak");

    /// <summary>Deletes the backup copies first, so a crash mid-delete can never resurrect an old version.</summary>
    public static void Delete(string path)
    {
        DeleteIfExists(path + ".bak");
        DeleteIfExists(path + ".tmp");
        DeleteIfExists(path);
    }

    /// <summary>File.Delete ignores a missing file but throws when its folder is missing.</summary>
    private static void DeleteIfExists(string file)
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}
