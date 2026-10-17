using BlindsDown.Core.Usage;
using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

public sealed class ConsentStoreMonitorTests : IDisposable
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly long AfterBoot = Boot.AddHours(1).ToFileTime();
    private static readonly long BeforeBoot = Boot.AddDays(-1).ToFileTime();

    private readonly FakeConsentStoreSource _source = new();
    private readonly FakeIdentityResolver _identities = new();
    private readonly List<string> _runningExes = [];
    private readonly ConsentStoreMonitor _monitor;

    public ConsentStoreMonitorTests()
    {
        _monitor = new ConsentStoreMonitor(_identities, _source, () => _runningExes, () => Boot);
    }

    public void Dispose() => _monitor.Dispose();

    [Fact]
    public void RunningPackagedAppInUseIsReported()
    {
        const string Family = "Contoso.VoiceNotes_8wekyb3d8bbwe";
        _identities.RunningPackages.Add(Family);
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, Family, true, AfterBoot, 0));

        UsageSignal signal = Assert.Single(_monitor.GetActiveSignals(CaptureKind.Microphone));

        Assert.Equal(CaptureKind.Microphone, signal.Kind);
        Assert.Equal(Family, signal.App.Key);
        Assert.Equal(Family, signal.App.PackageFamilyName);
        Assert.Equal("Voice Notes", signal.App.DisplayName);
        Assert.Equal(string.Empty, signal.DeviceId);
        Assert.True(signal.DeviceUnknown);
        Assert.Equal("Microphone", signal.DeviceName);
        Assert.Null(signal.ProcessId);
    }

    [Fact]
    public void PackagedAppThatIsNotRunningIsStale()
    {
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "Contoso.Crashed_abc", true, AfterBoot, 0));

        Assert.Empty(_monitor.GetActiveSignals(CaptureKind.Microphone));
    }

    [Fact]
    public void DesktopAppIsMatchedByEncodingTheRunningPathNeverByDecodingTheKey()
    {
        // The folder name contains '#', so decoding the key would give the wrong path.
        const string RunningPath = @"C:\Apps\Tool#2\tool.exe";
        _runningExes.Add(RunningPath);
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#Tool#2#tool.exe", false, AfterBoot, 0));

        UsageSignal signal = Assert.Single(_monitor.GetActiveSignals(CaptureKind.Microphone));

        Assert.Equal(RunningPath, signal.App.ExePath);
        Assert.Equal(RunningPath.ToLowerInvariant(), signal.App.Key);
        Assert.Equal("tool", signal.App.DisplayName);
        Assert.Null(signal.App.PackageFamilyName);
    }

    [Fact]
    public void DesktopKeyNamesMatchCaseInsensitively()
    {
        _runningExes.Add(@"C:\Program Files\Contoso\Recorder.exe");
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "c:#program files#contoso#RECORDER.EXE", false, AfterBoot, 0));

        Assert.Single(_monitor.GetActiveSignals(CaptureKind.Microphone));
    }

    [Fact]
    public void DesktopAppThatIsNotRunningIsStale()
    {
        _runningExes.Add(@"C:\Apps\Other\other.exe");
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#Gone#gone.exe", false, AfterBoot, 0));

        Assert.Empty(_monitor.GetActiveSignals(CaptureKind.Microphone));
    }

    [Fact]
    public void StoppedOrPreBootEntriesAreNotInUse()
    {
        _runningExes.Add(@"C:\Apps\a.exe");
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#a.exe", false, AfterBoot, AfterBoot + 1000));
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#a.exe", false, BeforeBoot, 0));

        Assert.Empty(_monitor.GetActiveSignals(CaptureKind.Microphone));
    }

    [Fact]
    public void CameraEntriesAreReportedAsCamera()
    {
        _runningExes.Add(@"C:\Apps\cam.exe");
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Camera, "C:#Apps#cam.exe", false, AfterBoot, 0));

        Assert.Empty(_monitor.GetActiveSignals(CaptureKind.Microphone));
        UsageSignal signal = Assert.Single(_monitor.GetActiveSignals(CaptureKind.Camera));
        Assert.Equal(CaptureKind.Camera, signal.Kind);
        Assert.Equal("Camera", signal.DeviceName);
    }

    [Fact]
    public void AnAppListedInBothHivesIsReportedOnce()
    {
        _runningExes.Add(@"C:\Windows\System32\svchost.exe");
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Windows#System32#svchost.exe", false, AfterBoot, 0));
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Windows#System32#svchost.exe", false, AfterBoot + 5, 0));

        Assert.Single(_monitor.GetActiveSignals(CaptureKind.Microphone));
    }

    [Fact]
    public void IdentityMatchesWhatTheProcessResolverProducesForTheSameApp()
    {
        string exe = SystemTestSupport.ProcessPath;
        _runningExes.Add(exe);
        _source.Entries.Add(new ConsentStoreEntry(
            CaptureKind.Microphone, ConsentStore.EncodeNonPackagedKeyName(exe).ToUpperInvariant(), false, AfterBoot, 0));

        UsageSignal signal = Assert.Single(_monitor.GetActiveSignals(CaptureKind.Microphone));
        AppIdentity live = new ProcessIdentityResolver().Resolve(Environment.ProcessId);

        Assert.Equal(live.Key, signal.App.Key);
        Assert.Equal(live.DisplayName, signal.App.DisplayName);
        Assert.Equal(live.GroupKey, signal.App.GroupKey);
    }

    [Fact]
    public void LastUsedRecordsKeepCompletedUsesSinceTheCutoffNewestFirst()
    {
        DateTimeOffset since = Boot.AddDays(-7);
        long older = Boot.AddDays(-3).ToFileTime();
        long newer = Boot.AddDays(-1).ToFileTime();
        _runningExes.Add(@"C:\Apps\Chat#1\chat.exe");
        _source.Entries.AddRange(
        [
            new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#Chat#1#chat.exe", false, older, older + TimeSpan.TicksPerMinute),
            new ConsentStoreEntry(CaptureKind.Camera, "Contoso.Camera_xyz", true, newer, newer + TimeSpan.TicksPerHour),
            new ConsentStoreEntry(CaptureKind.Microphone, "C:#Old#old.exe", false, Boot.AddDays(-30).ToFileTime(), newer),
            new ConsentStoreEntry(CaptureKind.Microphone, "C:#Live#live.exe", false, newer, 0),
            new ConsentStoreEntry(CaptureKind.Microphone, "C:#Odd#odd.exe", false, newer, newer - 1),
        ]);

        IReadOnlyList<UsageRecord> records = _monitor.GetLastUsedRecords(since);

        Assert.Equal(2, records.Count);
        UsageRecord camera = records[0];
        Assert.Equal(CaptureKind.Camera, camera.Kind);
        Assert.Equal("Camera", camera.DeviceName);
        Assert.Equal("pkg:CONTOSO.CAMERA_XYZ", camera.AppKey);
        Assert.Equal(DateTimeOffset.FromFileTime(newer), camera.Start);
        Assert.Equal(DateTimeOffset.FromFileTime(newer + TimeSpan.TicksPerHour), camera.End);
        Assert.True(camera.Approximate);

        UsageRecord mic = records[1];
        Assert.Equal(CaptureKind.Microphone, mic.Kind);
        Assert.Equal(@"C:\Apps\Chat#1\chat.exe", mic.ExePath);
        Assert.Equal("chat", mic.AppName);
        Assert.Equal(AppIdentityRules.ForExePath(@"C:\Apps\Chat#1\chat.exe").GroupKey, mic.AppKey);
        Assert.True(mic.Approximate);
    }

    [Fact]
    public void LastUsedRecordsDecodeKeysOfAppsThatAreNotRunningForDisplay()
    {
        long start = Boot.AddDays(-1).ToFileTime();
        _source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#Recorder#rec.exe", false, start, start + 10));

        UsageRecord record = Assert.Single(_monitor.GetLastUsedRecords(Boot.AddDays(-2)));

        Assert.Equal(@"C:\Apps\Recorder\rec.exe", record.ExePath);
        Assert.Equal("rec", record.AppName);
    }

    [Fact]
    public void RegistryFailuresDegradeToNothing()
    {
        _source.Throw = true;

        Assert.Empty(_monitor.GetActiveSignals(CaptureKind.Microphone));
        Assert.Empty(_monitor.GetLastUsedRecords(DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task BurstsOfRegistryChangesRaiseOneDebouncedChanged()
    {
        int raised = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _monitor.Changed += () =>
        {
            Interlocked.Increment(ref raised);
            first.TrySetResult();
        };
        _monitor.Start();
        Assert.Equal(1, _source.EnsureWatchingCalls);

        for (int i = 0; i < 5; i++)
        {
            _source.SimulateChange();
        }

        await first.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task PollNoticesAnAppThatExitedWithoutRecordingAStop()
    {
        // An app that dies mid-use leaves Stop == 0 and the registry never changes again: only the poll can
        // see that its process is gone.
        var running = new ThreadSafeList(@"C:\Apps\rec.exe");
        var source = new FakeConsentStoreSource();
        source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#rec.exe", false, AfterBoot, 0));
        using var monitor = new ConsentStoreMonitor(_identities, source, running.Snapshot, () => Boot, TimeSpan.FromMilliseconds(50));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += () => changed.TrySetResult();
        monitor.Start();
        Assert.Single(monitor.GetActiveSignals(CaptureKind.Microphone));

        running.Clear();

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(monitor.GetActiveSignals(CaptureKind.Microphone));

        // Each poll also re-arms watches that broke or whose key did not exist yet.
        Assert.True(source.EnsureWatchingCalls > 1);
    }

    [Fact]
    public async Task PollStaysQuietWhileNothingChanges()
    {
        var running = new ThreadSafeList(@"C:\Apps\rec.exe");
        var source = new FakeConsentStoreSource();
        source.Entries.Add(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#rec.exe", false, AfterBoot, 0));
        using var monitor = new ConsentStoreMonitor(_identities, source, running.Snapshot, () => Boot, TimeSpan.FromMilliseconds(20));
        int raised = 0;
        monitor.Changed += () => Interlocked.Increment(ref raised);
        monitor.Start();

        // Wait for several polls (each one calls EnsureWatching) rather than a fixed time, so a slow CI
        // machine can't make the test pass without polling.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (source.EnsureWatchingCalls < 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        Assert.True(source.EnsureWatchingCalls >= 5);
        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task NoChangedAfterDispose()
    {
        int raised = 0;
        _monitor.Changed += () => Interlocked.Increment(ref raised);
        _monitor.Start();
        _monitor.Dispose();

        _source.SimulateChange();
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref raised));
        Assert.True(_source.Disposed);
    }

    /// <summary>Running exe paths that the test thread changes while the monitor's poll thread reads them.</summary>
    private sealed class ThreadSafeList(params string[] initial)
    {
        private readonly List<string> _items = [.. initial];

        public IReadOnlyCollection<string> Snapshot()
        {
            lock (_items)
            {
                return [.. _items];
            }
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
            }
        }
    }
}
