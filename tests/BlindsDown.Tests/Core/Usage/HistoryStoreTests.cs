using BlindsDown.Core.Usage;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Usage;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = FakeClock.Epoch;

    private readonly TempDirectory _temp = new();
    private readonly string _path;
    private readonly HistoryStore _store;

    public HistoryStoreTests()
    {
        _path = Path.Combine(_temp.Path, "nested", "history.jsonl");
        _store = new HistoryStore(_path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        Assert.Empty(_store.ReadAll());
        _store.Prune(T0);
        _store.Clear();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AppendWritesOneCompactLinePerRecordAndReadsNewestFirst()
    {
        _store.Append(Record("Zoom", 0, 45));
        _store.Append(Record("Discord", 60, 90, approximate: true));
        _store.Append(Record("Teams", 30, 40));

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{\"kind\":\"Microphone\"", l, StringComparison.Ordinal));
        Assert.Contains("\"approximate\":true", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("exePath", lines[0], StringComparison.Ordinal);

        List<UsageRecord> records = _store.ReadAll();
        Assert.Equal(["Discord", "Teams", "Zoom"], records.Select(r => r.AppName));
        Assert.Equal(Record("Discord", 60, 90, approximate: true), records[0]);
    }

    [Fact]
    public void DamagedLinesAreSkipped()
    {
        _store.Append(Record("Zoom", 0, 10));
        File.AppendAllText(_path, "\n   \n{ not json\n{\"kind\":\"Microphone\",\"appKey\":\"x\"}\n{\"kind\":\"Telepathy\",\"appKey\":\"k\",\"appName\":\"n\",\"deviceName\":\"d\",\"start\":\"2026-10-17T12:00:00+00:00\",\"end\":\"2026-10-17T12:00:00+00:00\"}\nnull\n");
        _store.Append(Record("Teams", 20, 30));

        Assert.Equal(["Teams", "Zoom"], _store.ReadAll().Select(r => r.AppName));
    }

    [Fact]
    public void RecordAfterAPowerCutMidLineIsNotLost()
    {
        // The last write was cut off: no closing brace and no newline.
        _store.Append(Record("Zoom", 0, 10));
        File.AppendAllText(_path, "{\"kind\":\"Microphone\",\"appKey\":\"exe:TE");

        _store.Append(Record("Teams", 20, 30));
        _store.Append(Record("Slack", 40, 50));

        Assert.Equal(["Slack", "Teams", "Zoom"], _store.ReadAll().Select(r => r.AppName));
    }

    [Fact]
    public void PruneDropsWhatEndedBeforeTheCutoffAndLeavesNoBackup()
    {
        _store.Append(Record("Old", 0, 10));
        _store.Append(Record("Edge", 20, 30));
        _store.Append(Record("New", 100, 200));
        File.AppendAllText(_path, "damaged\n");

        _store.Prune(T0.AddSeconds(30));

        Assert.Equal(["New", "Edge"], _store.ReadAll().Select(r => r.AppName));
        Assert.False(File.Exists(_path + ".bak"));
        Assert.False(File.Exists(_path + ".tmp"));

        // Oldest first on disk, so appends keep the file roughly in order.
        Assert.StartsWith("{\"kind\":\"Microphone\",\"appKey\":\"exe:EDGE.EXE|EDGE\"", File.ReadAllLines(_path)[0], StringComparison.Ordinal);

        _store.Append(Record("Later", 300, 310));
        Assert.Equal(3, _store.ReadAll().Count);
    }

    [Fact]
    public void PruneEverythingLeavesAnEmptyFileThatStillWorks()
    {
        _store.Append(Record("Old", 0, 10));

        _store.Prune(T0.AddDays(1));

        Assert.Empty(_store.ReadAll());
        _store.Append(Record("New", 0, 10));
        Assert.Single(_store.ReadAll());
    }

    [Fact]
    public void ClearDeletesTheHistory()
    {
        _store.Append(Record("Zoom", 0, 10));

        _store.Clear();

        Assert.Empty(_store.ReadAll());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void NonAsciiNamesRoundTrip()
    {
        _store.Append(Record("Téléphone 電話", 0, 10));

        Assert.Equal("Téléphone 電話", _store.ReadAll()[0].AppName);
        Assert.All(File.ReadAllBytes(_path), b => Assert.True(b < 0x80));
    }

    [Fact]
    public void MergeJoinsShortGapsOfTheSameAppAndDevice()
    {
        List<UsageRecord> merged = HistoryStore.MergeShortGaps(
            [
                Record("Zoom", 0, 60),
                Record("Zoom", 70, 120),
                Record("Zoom", 135, 200, approximate: true),
                Record("Zoom", 216, 300),
            ],
            TimeSpan.FromSeconds(15));

        Assert.Equal(2, merged.Count);
        Assert.Equal((T0.AddSeconds(216), T0.AddSeconds(300)), (merged[0].Start, merged[0].End));
        Assert.Equal((T0, T0.AddSeconds(200)), (merged[1].Start, merged[1].End));
        Assert.True(merged[1].Approximate);
        Assert.False(merged[0].Approximate);
    }

    [Fact]
    public void MergeHandlesOverlapsAndUnorderedInput()
    {
        List<UsageRecord> merged = HistoryStore.MergeShortGaps(
            [Record("Zoom", 50, 70), Record("Zoom", 0, 100), Record("Zoom", 10, 20)],
            TimeSpan.FromSeconds(15));

        UsageRecord only = Assert.Single(merged);
        Assert.Equal((T0, T0.AddSeconds(100)), (only.Start, only.End));
    }

    [Fact]
    public void MergeNeverJoinsDifferentAppsDevicesOrKinds()
    {
        List<UsageRecord> merged = HistoryStore.MergeShortGaps(
            [
                Record("Zoom", 0, 10),
                Record("Teams", 11, 20),
                Record("Zoom", 12, 20) with { DeviceName = "Webcam mic" },
                Record("Zoom", 13, 20) with { Kind = CaptureKind.Camera },
                Record("ZOOM", 14, 30),
            ],
            TimeSpan.FromSeconds(15));

        Assert.Equal(4, merged.Count);
        Assert.Equal(T0.AddSeconds(30), merged.Single(r => r.AppName == "Zoom" && r.Kind == CaptureKind.Microphone && r.DeviceName == "Arctis").End);
        Assert.Equal(merged.OrderByDescending(r => r.Start), merged);
    }

    [Fact]
    public void MergeOfNothingIsNothing()
    {
        Assert.Empty(HistoryStore.MergeShortGaps([], TimeSpan.FromSeconds(15)));
    }

    private static UsageRecord Record(string app, int startSeconds, int endSeconds, bool approximate = false) => new()
    {
        Kind = CaptureKind.Microphone,
        AppKey = $"exe:{app.ToUpperInvariant()}.EXE|{app.ToUpperInvariant()}",
        AppName = app,
        DeviceName = "Arctis",
        Start = T0.AddSeconds(startSeconds),
        End = T0.AddSeconds(endSeconds),
        Approximate = approximate,
    };
}
