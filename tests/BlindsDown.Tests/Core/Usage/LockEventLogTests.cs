using BlindsDown.Core.Usage;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Usage;

public sealed class LockEventLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = FakeClock.Epoch;

    private readonly TempDirectory _temp = new();
    private readonly string _path;
    private readonly LockEventLog _log;

    public LockEventLogTests()
    {
        _path = Path.Combine(_temp.Path, "nested", "lock-events.jsonl");
        _log = new LockEventLog(_path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        Assert.Empty(_log.ReadAll());
        _log.Prune(T0);
        _log.Clear();
        Assert.False(File.Exists(_path));
        Assert.Equal(_path, _log.ToString());
    }

    [Fact]
    public void AppendWritesOneCompactLinePerRecordAndReadsNewestFirst()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey, "4 microphones"));
        _log.Append(Unlock(60, LockEventSource.Tray, "All 4 microphones restored exactly as before."));
        _log.Append(Lock(120, LockEventSource.Automatic, hadProblems: true));

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(3, lines.Length);
        Assert.Equal("{\"at\":\"2026-10-17T12:00:00+00:00\",\"action\":\"Lock\",\"source\":\"Hotkey\",\"summary\":\"4 microphones\",\"hadProblems\":false}", lines[0]);
        Assert.DoesNotContain("summary", lines[2], StringComparison.Ordinal);

        List<LockEventRecord> records = _log.ReadAll();
        Assert.Equal([T0.AddSeconds(120), T0.AddSeconds(60), T0], records.Select(r => r.At));
        Assert.Equal(Unlock(60, LockEventSource.Tray, "All 4 microphones restored exactly as before."), records[1]);
        Assert.True(records[0].HadProblems);
    }

    [Fact]
    public void RecordsAtTheSameMomentKeepTheirOrder()
    {
        // A lock immediately undone (e.g. "--toggle" twice) must not show the unlock before the lock.
        _log.Append(Lock(0, LockEventSource.OtherProgram));
        _log.Append(Unlock(0, LockEventSource.OtherProgram));

        Assert.Equal([LockEventAction.Unlock, LockEventAction.Lock], _log.ReadAll().Select(r => r.Action));
    }

    [Fact]
    public void DamagedLinesAreSkipped()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey));
        File.AppendAllText(
            _path,
            "\n   \n{ not json\n{\"at\":\"2026-10-17T12:00:00+00:00\",\"source\":\"Tray\"}\n"
            + "{\"at\":\"2026-10-17T12:00:00+00:00\",\"action\":\"Explode\",\"source\":\"Tray\"}\nnull\n");
        _log.Append(Unlock(30, LockEventSource.Tray));

        Assert.Equal([LockEventAction.Unlock, LockEventAction.Lock], _log.ReadAll().Select(r => r.Action));
    }

    [Fact]
    public void RecordAfterAPowerCutMidLineIsNotLost()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey));
        File.AppendAllText(_path, "{\"at\":\"2026-10-17T12:00:00+00:00\",\"act");

        _log.Append(Unlock(30, LockEventSource.Hotkey));
        _log.Append(Lock(60, LockEventSource.Recovery));

        Assert.Equal([T0.AddSeconds(60), T0.AddSeconds(30), T0], _log.ReadAll().Select(r => r.At));
    }

    [Fact]
    public void PruneDropsWhatHappenedBeforeTheCutoffAndLeavesNoBackup()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey));
        _log.Append(Unlock(20, LockEventSource.Hotkey));
        _log.Append(Lock(100, LockEventSource.Exit));
        File.AppendAllText(_path, "damaged\n");

        _log.Prune(T0.AddSeconds(20));

        Assert.Equal([T0.AddSeconds(100), T0.AddSeconds(20)], _log.ReadAll().Select(r => r.At));
        Assert.False(File.Exists(_path + ".bak"));
        Assert.False(File.Exists(_path + ".tmp"));

        // Oldest first on disk, so appends keep the file in order.
        Assert.StartsWith("{\"at\":\"2026-10-17T12:00:20+00:00\"", File.ReadAllLines(_path)[0], StringComparison.Ordinal);
        _log.Append(Unlock(300, LockEventSource.Exit));
        Assert.Equal(3, _log.ReadAll().Count);
    }

    [Fact]
    public void PruneEverythingLeavesAnEmptyFileThatStillWorks()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey));

        _log.Prune(T0.AddDays(1));

        Assert.Empty(_log.ReadAll());
        _log.Append(Unlock(0, LockEventSource.Hotkey));
        Assert.Single(_log.ReadAll());
    }

    [Fact]
    public void ClearDeletesTheLog()
    {
        _log.Append(Lock(0, LockEventSource.Hotkey));

        _log.Clear();

        Assert.Empty(_log.ReadAll());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void NonAsciiSummariesRoundTrip()
    {
        _log.Append(Unlock(0, LockEventSource.Tray, "Mikrofon (Realtek(R) Audio) – 電話"));

        Assert.Equal("Mikrofon (Realtek(R) Audio) – 電話", _log.ReadAll()[0].Summary);
        Assert.All(File.ReadAllBytes(_path), b => Assert.True(b < 0x80));
    }

    [Fact]
    public void ConcurrentAppendsFromTwoThreadsAreAllKept()
    {
        Parallel.For(0, 200, i => _log.Append(Lock(i, i % 2 == 0 ? LockEventSource.Hotkey : LockEventSource.Tray)));

        Assert.Equal(200, _log.ReadAll().Count);
        Assert.All(File.ReadAllLines(_path), l => Assert.StartsWith("{\"at\":", l, StringComparison.Ordinal));
    }

    private static LockEventRecord Lock(int seconds, LockEventSource source, string? summary = null, bool hadProblems = false) => new()
    {
        At = T0.AddSeconds(seconds),
        Action = LockEventAction.Lock,
        Source = source,
        Summary = summary,
        HadProblems = hadProblems,
    };

    private static LockEventRecord Unlock(int seconds, LockEventSource source, string? summary = null) => new()
    {
        At = T0.AddSeconds(seconds),
        Action = LockEventAction.Unlock,
        Source = source,
        Summary = summary,
    };
}
