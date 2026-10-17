using System.Text;
using BlindsDown.Core.IO;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.IO;

/// <summary>
/// The crash-safe write: "file.tmp" is written and flushed, then replaces "file" (old version kept as
/// "file.bak"). These tests recreate every on-disk state a crash can leave behind.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FirstWriteCreatesTheFileAndItsDirectoryWithoutLeftovers()
    {
        string path = Path.Combine(_temp.Path, "sub", "dir", "state.json");

        AtomicFile.WriteAllText(path, "v1");

        Assert.Equal("v1", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
        Assert.Equal(["v1"], AtomicFile.ReadCandidates(path));
    }

    [Fact]
    public void ReplaceKeepsThePreviousVersionAsBackup()
    {
        string path = _temp.File("state.json");

        AtomicFile.WriteAllText(path, "v1");
        AtomicFile.WriteAllText(path, "v2");
        AtomicFile.WriteAllText(path, "v3");

        Assert.Equal("v3", File.ReadAllText(path));
        Assert.Equal("v2", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(["v3", "v2"], AtomicFile.ReadCandidates(path));
    }

    [Fact]
    public void WritesUtf8WithoutByteOrderMark()
    {
        string path = _temp.File("state.json");

        AtomicFile.WriteAllText(path, "Mikrofon (Hörsprechgarnitur)");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Mikrofon (Hörsprechgarnitur)", Encoding.UTF8.GetString(bytes));
        Assert.Equal("Mikrofon (Hörsprechgarnitur)", AtomicFile.ReadCandidates(path).Single());
    }

    [Fact]
    public void StaleTempFileFromACrashIsOverwrittenByTheNextWrite()
    {
        string path = _temp.File("state.json");
        AtomicFile.WriteAllText(path, "v1");
        File.WriteAllText(path + ".tmp", "{ half writ");

        AtomicFile.WriteAllText(path, "v2");

        Assert.Equal("v2", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void CrashBeforeReplaceKeepsTheCommittedFileFirst()
    {
        // Killed after the temp file was written but before it replaced the file: the save never
        // returned, so the committed file is still the truth; the temp copy is a fallback.
        string path = _temp.File("state.json");
        AtomicFile.WriteAllText(path, "v1");
        AtomicFile.WriteAllText(path, "v2");
        File.WriteAllText(path + ".tmp", "v3");

        Assert.Equal(["v2", "v3", "v1"], AtomicFile.ReadCandidates(path));
    }

    [Fact]
    public void CrashDuringReplaceFallsBackToTheTempCopyThenTheBackup()
    {
        // ReplaceFile moved the file to .bak but had not yet moved .tmp into place.
        string path = _temp.File("state.json");
        File.WriteAllText(path + ".tmp", "v3");
        File.WriteAllText(path + ".bak", "v2");

        Assert.True(AtomicFile.AnyCopyExists(path));
        Assert.Equal(["v3", "v2"], AtomicFile.ReadCandidates(path));
    }

    [Fact]
    public void EmptyAndWhitespaceCopiesAreSkipped()
    {
        // NTFS can leave a zero-length file after a power cut.
        string path = _temp.File("state.json");
        File.WriteAllText(path, string.Empty);
        File.WriteAllText(path + ".tmp", "  \r\n ");
        File.WriteAllText(path + ".bak", "v1");

        Assert.Equal(["v1"], AtomicFile.ReadCandidates(path));
    }

    [Fact]
    public void LockedCopyIsSkipped()
    {
        string path = _temp.File("state.json");
        AtomicFile.WriteAllText(path, "v1");
        AtomicFile.WriteAllText(path, "v2");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(["v1"], AtomicFile.ReadCandidates(path));
        }
    }

    [Fact]
    public void NoCopiesMeansNothing()
    {
        string path = _temp.File("missing.json");

        Assert.False(AtomicFile.AnyCopyExists(path));
        Assert.Empty(AtomicFile.ReadCandidates(path));
        AtomicFile.Delete(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tmp")]
    [InlineData(".bak")]
    public void AnySingleCopyCounts(string suffix)
    {
        string path = _temp.File("state.json");
        File.WriteAllText(path + suffix, "x");

        Assert.True(AtomicFile.AnyCopyExists(path));
    }

    [Fact]
    public void DeleteRemovesEveryCopy()
    {
        string path = _temp.File("state.json");
        AtomicFile.WriteAllText(path, "v1");
        AtomicFile.WriteAllText(path, "v2");
        File.WriteAllText(path + ".tmp", "v3");

        AtomicFile.Delete(path);

        Assert.False(AtomicFile.AnyCopyExists(path));
    }

    [Fact]
    public void DeleteRemovesTheBackupsBeforeTheFile()
    {
        // If deleting is interrupted, an older version must never be left as the only copy.
        string path = _temp.File("state.json");
        AtomicFile.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        File.WriteAllText(path + ".tmp", "newer");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.Delete(path));

        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(["new"], AtomicFile.ReadCandidates(path));
    }
}
