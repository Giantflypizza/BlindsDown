using BlindsDown.Core.Usage;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Usage;

public sealed class UsageTrackerTests
{
    private const string Sessions = "audio-sessions";
    private const string Registry = "usage-registry";

    private static readonly AppIdentity Zoom = new(@"c:\program files\zoom\bin\zoom.exe", "Zoom", @"C:\Program Files\Zoom\bin\Zoom.exe", null);
    private static readonly AppIdentity Discord = new("discord", "Discord", @"C:\Users\u\AppData\Local\Discord\app-1.0.9262\Discord.exe", null);
    private static readonly AppIdentity Camera = new("pkg", "Camera", null, "Microsoft.WindowsCamera_8wekyb3d8bbwe");

    private readonly FakeClock _clock = new();
    private readonly List<string> _events = [];
    private readonly List<UsageSession> _ended = [];
    private readonly UsageTracker _tracker;

    public UsageTrackerTests()
    {
        _tracker = new UsageTracker(_clock);
        _tracker.Started += s => _events.Add($"started {s.App.DisplayName} on {Device(s)}");
        _tracker.Confirmed += s => _events.Add($"confirmed {s.App.DisplayName} on {Device(s)}");
        _tracker.Ended += s =>
        {
            _events.Add($"ended {s.App.DisplayName} on {Device(s)}");
            _ended.Add(s);
        };
    }

    [Fact]
    public void DefaultsMatchThePlan()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), _tracker.StopGrace);
        Assert.Equal(TimeSpan.FromSeconds(1), _tracker.ConfirmAfter);
    }

    [Fact]
    public void StartConfirmAndEndHappenOnTime()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis", pid: 42)]);
        Assert.Equal(["started Zoom on arctis"], _events);
        UsageSession active = Assert.Single(_tracker.ActiveSessions);
        Assert.Equal(42, active.ProcessId);
        Assert.Equal(FakeClock.Epoch, active.Start);
        Assert.Null(active.End);

        _clock.AdvanceSeconds(0.9);
        _tracker.Tick();
        Assert.Single(_events);

        _clock.AdvanceSeconds(0.1);
        _tracker.Tick();
        Assert.Equal("confirmed Zoom on arctis", _events[^1]);

        _clock.AdvanceSeconds(60);
        _tracker.Report(Sessions, []);
        DateTimeOffset stoppedAt = _clock.UtcNow;
        Assert.Empty(_tracker.ActiveSessions);
        Assert.Equal(2, _events.Count);

        _clock.AdvanceSeconds(2.9);
        _tracker.Tick();
        Assert.Equal(2, _events.Count);

        _clock.AdvanceSeconds(0.1);
        _tracker.Tick();
        Assert.Equal("ended Zoom on arctis", _events[^1]);
        UsageSession ended = Assert.Single(_ended);
        Assert.Equal(stoppedAt, ended.End);
        Assert.Equal(TimeSpan.FromSeconds(61), ended.Duration(_clock.UtcNow));

        // Confirmed only once.
        Assert.Single(_events, e => e.StartsWith("confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortProbeIsNeverConfirmed()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        _clock.AdvanceSeconds(0.4);
        _tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(5);
        _tracker.Tick();

        Assert.Equal(["started Zoom on arctis", "ended Zoom on arctis"], _events);
        Assert.Equal(TimeSpan.FromSeconds(0.4), _ended[0].Duration(_clock.UtcNow));
    }

    [Fact]
    public void FlapsWithinTheGracePeriodAreOneSession()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        Guid id = _tracker.ActiveSessions[0].Id;

        for (int i = 0; i < 5; i++)
        {
            _clock.AdvanceSeconds(1);
            _tracker.Report(Sessions, []);
            _clock.AdvanceSeconds(2);
            _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        }

        Assert.Equal(id, Assert.Single(_tracker.ActiveSessions).Id);
        Assert.Equal(["started Zoom on arctis", "confirmed Zoom on arctis"], _events);
    }

    [Fact]
    public void GapLongerThanTheGracePeriodIsTwoSessions()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        _clock.AdvanceSeconds(1);
        _tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(3);
        _tracker.Tick();
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);

        Assert.Equal(2, _events.Count(e => e.StartsWith("started", StringComparison.Ordinal)));
        Assert.Single(_ended);
    }

    [Fact]
    public void SameUseSeenByTwoDeviceAwareSourcesIsOneSession()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        _tracker.Report("second-source", [Mic(Zoom, "arctis")]);

        UsageSession session = Assert.Single(_tracker.ActiveSessions);
        Assert.Equal([Sessions, "second-source"], session.Sources.Order(StringComparer.Ordinal));

        _tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(10);
        _tracker.Tick();
        Assert.Single(_tracker.ActiveSessions);
        Assert.Equal(["second-source"], _tracker.ActiveSessions[0].Sources);

        _tracker.Report("second-source", []);
        _clock.AdvanceSeconds(10);
        _tracker.Tick();
        Assert.Empty(_tracker.ActiveSessions);
        Assert.Single(_ended);
    }

    [Fact]
    public void DifferentAppsDevicesAndKindsAreSeparateSessions()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis"), Mic(Zoom, "sonar"), Mic(Discord, "sonar")]);
        _tracker.Report("camera-monitor", [Cam(Camera, "cam")]);

        Assert.Equal(4, _tracker.ActiveSessions.Count);
    }

    [Fact]
    public void RegistrySignalIsSuppressedWhenADeviceAwareSourceReportsTheSameApp()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        _tracker.Report(Registry, [Unknown(Zoom)]);

        UsageSession session = Assert.Single(_tracker.ActiveSessions);
        Assert.Equal("arctis", session.DeviceId);
        Assert.Equal([Sessions], session.Sources);
        Assert.Single(_events);
    }

    [Fact]
    public void RegistrySignalCountsWhenNoDeviceAwareSourceSeesTheApp()
    {
        _tracker.Report(Sessions, [Mic(Discord, "sonar")]);
        _tracker.Report(Registry, [Unknown(Zoom), Unknown(Discord, CaptureKind.Camera)]);

        Assert.Equal(3, _tracker.ActiveSessions.Count);
        Assert.Contains(_tracker.ActiveSessions, s => s.App == Zoom && s.DeviceId.Length == 0);
        Assert.Contains(_tracker.ActiveSessions, s => s.App == Discord && s.Kind == CaptureKind.Camera);
    }

    [Fact]
    public void RegistryFirstThenDeviceAwareIsStillOneUse()
    {
        // The registry often notices a new use a moment before the audio session shows up.
        _tracker.Report(Registry, [Unknown(Zoom)]);
        UsageSession first = Assert.Single(_tracker.ActiveSessions);
        _clock.AdvanceSeconds(0.5);
        _tracker.Report(Sessions, [Mic(Zoom, "arctis", pid: 42)]);

        UsageSession session = Assert.Single(_tracker.ActiveSessions);
        Assert.Equal(first.Id, session.Id);
        Assert.Equal(first.Start, session.Start);
        Assert.Equal("arctis", session.DeviceId);
        Assert.Equal("Microphone (arctis)", session.DeviceName);
        Assert.Equal(42, session.ProcessId);

        _clock.AdvanceSeconds(10);
        _tracker.Tick();
        Assert.Empty(_ended);
        Assert.Single(_events, e => e.StartsWith("started", StringComparison.Ordinal));
        Assert.Single(_events, e => e.StartsWith("confirmed", StringComparison.Ordinal));
        Assert.Equal("confirmed Zoom on arctis", _events[^1]);
    }

    [Fact]
    public void RegistryLaggingBehindTheEndOfAUseDoesNotStartANewOne()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        _tracker.Report(Registry, [Unknown(Zoom)]);
        _clock.AdvanceSeconds(30);
        _tracker.Tick();

        // The audio session ends; the registry's "stop" time is written a second later.
        _tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(1);
        _tracker.Tick();
        _tracker.Report(Registry, []);
        _clock.AdvanceSeconds(10);
        _tracker.Tick();

        Assert.Single(_events, e => e.StartsWith("started", StringComparison.Ordinal));
        UsageSession ended = Assert.Single(_ended);
        Assert.Equal("arctis", ended.DeviceId);
        Assert.Empty(_tracker.ActiveSessions);
    }

    [Fact]
    public void ProcessIdIsKeptWhenALaterReportHasNone()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis", pid: 42)]);
        _tracker.Report(Sessions, [Mic(Zoom, "arctis", pid: null)]);
        Assert.Equal(42, _tracker.ActiveSessions[0].ProcessId);

        _tracker.Report(Sessions, [Mic(Zoom, "arctis", pid: 43)]);
        Assert.Equal(43, _tracker.ActiveSessions[0].ProcessId);
    }

    [Fact]
    public void EndAllClosesEverySessionAtTheRightTime()
    {
        _tracker.Report(Sessions, [Mic(Zoom, "arctis"), Mic(Discord, "sonar")]);
        _clock.AdvanceSeconds(5);
        _tracker.Report(Sessions, [Mic(Discord, "sonar")]);
        DateTimeOffset zoomStopped = _clock.UtcNow;
        _clock.AdvanceSeconds(1);

        _tracker.EndAll();

        Assert.Equal(2, _ended.Count);
        Assert.Equal(zoomStopped, _ended.Single(s => s.App == Zoom).End);
        Assert.Equal(_clock.UtcNow, _ended.Single(s => s.App == Discord).End);
        Assert.Empty(_tracker.ActiveSessions);

        // Nothing is resurrected from the old reports.
        _tracker.Tick();
        Assert.Empty(_tracker.ActiveSessions);
    }

    [Fact]
    public void HandlersMayCallBackIntoTheTracker()
    {
        var tracker = new UsageTracker(_clock);
        int confirmed = 0;
        tracker.Confirmed += _ =>
        {
            confirmed++;
            tracker.Report("other", []);
        };

        tracker.Report(Sessions, [Mic(Zoom, "arctis"), Mic(Discord, "sonar")]);
        _clock.AdvanceSeconds(2);
        tracker.Tick();

        Assert.Equal(2, confirmed);
    }

    [Fact]
    public void HandlersMayCallBackIntoTheTrackerWhenSessionsEnd()
    {
        var tracker = new UsageTracker(_clock);
        var ended = new List<string>();
        tracker.Ended += s =>
        {
            ended.Add(s.App.DisplayName);
            tracker.Tick();
        };

        tracker.Report(Sessions, [Mic(Zoom, "arctis"), Mic(Discord, "sonar")]);
        tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(5);
        tracker.Tick();

        Assert.Equal(["Discord", "Zoom"], ended.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SessionEndedByAHandlerIsNotConfirmedAfterItsEnd()
    {
        var tracker = new UsageTracker(_clock, stopGrace: TimeSpan.Zero);
        var events = new List<string>();
        tracker.Ended += s => events.Add("ended " + s.App.DisplayName);
        tracker.Confirmed += s =>
        {
            // The handler's refresh no longer lists the other app, which ends it right away.
            events.Add("confirmed " + s.App.DisplayName);
            tracker.Report(Sessions, [new UsageSignal(s.Kind, s.App, s.ProcessId, s.DeviceId, s.DeviceName)]);
        };

        tracker.Report(Sessions, [Mic(Zoom, "arctis"), Mic(Discord, "sonar")]);
        _clock.AdvanceSeconds(2);
        tracker.Tick();

        Assert.Equal(2, events.Count);
        Assert.StartsWith("confirmed ", events[0], StringComparison.Ordinal);
        Assert.StartsWith("ended ", events[1], StringComparison.Ordinal);
        Assert.NotEqual(events[0][10..], events[1][6..]);
        Assert.Single(tracker.ActiveSessions);
    }

    [Fact]
    public void CustomTimingIsHonoured()
    {
        var tracker = new UsageTracker(_clock, stopGrace: TimeSpan.FromSeconds(10), confirmAfter: TimeSpan.Zero);
        int confirmed = 0;
        int ended = 0;
        tracker.Confirmed += _ => confirmed++;
        tracker.Ended += _ => ended++;

        tracker.Report(Sessions, [Mic(Zoom, "arctis")]);
        Assert.Equal(1, confirmed);
        tracker.Report(Sessions, []);
        _clock.AdvanceSeconds(9);
        tracker.Tick();
        Assert.Equal(0, ended);
        _clock.AdvanceSeconds(1);
        tracker.Tick();
        Assert.Equal(1, ended);
    }

    [Fact]
    public void UsageRecordUsesTheGroupKey()
    {
        _tracker.Report(Sessions, [Mic(Discord, "sonar") with { ViaApp = "SteelSeries Sonar" }]);
        UsageSession session = _tracker.ActiveSessions[0];

        UsageRecord record = UsageRecord.From(session, _clock.UtcNow.AddMinutes(5));

        Assert.Equal(@"exe:C:\USERS\U\APPDATA\LOCAL\DISCORD\*\DISCORD.EXE", record.AppKey);
        Assert.Equal("Discord", record.AppName);
        Assert.Equal("SteelSeries Sonar", record.ViaApp);
        Assert.Equal(TimeSpan.FromMinutes(5), record.End - record.Start);
        Assert.False(record.Approximate);
    }

    [Fact]
    public void GroupKeysIgnoreVersionedFolders()
    {
        AppIdentity older = Discord with { ExePath = @"C:\Users\u\AppData\Local\Discord\app-1.0.9100\Discord.exe", Key = "older" };

        Assert.Equal(Discord.GroupKey, older.GroupKey);
        Assert.Equal("pkg:MICROSOFT.WINDOWSCAMERA_8WEKYB3D8BBWE", Camera.GroupKey);
        Assert.Equal("exe:UNKNOWN|UNKNOWN APP", AppIdentity.Unknown.GroupKey);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\118.0.2088.46\msedgewebview2.exe", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\*\msedgewebview2.exe")]
    [InlineData(@"C:\Apps\Tool\v2.1.0\tool.exe", @"C:\Apps\Tool\*\tool.exe")]
    [InlineData(@"C:\Apps\Tool\1.2.3-beta.1\tool.exe", @"C:\Apps\Tool\*\tool.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Games\2024\game.exe", @"C:\Games\2024\game.exe")]
    public void VersionFoldersAreNormalized(string path, string expected) =>
        Assert.Equal(expected, AppIdentity.WithoutVersionFolders(path));

    [Fact]
    public void AnImpostorWithTheSameFileNameElsewhereDoesNotShareTheGroupKey()
    {
        AppIdentity sonar = new(@"c:\program files\steelseries\gg\apps\sonar\steelseriessonar.exe", "SteelSeries Sonar", @"C:\Program Files\SteelSeries\GG\apps\sonar\SteelSeriesSonar.exe", null);
        AppIdentity impostor = new(@"c:\users\u\appdata\local\temp\x\steelseriessonar.exe", "SteelSeries Sonar", @"C:\Users\u\AppData\Local\Temp\x\SteelSeriesSonar.exe", null);

        Assert.NotEqual(sonar.GroupKey, impostor.GroupKey);
    }

    private static UsageSignal Mic(AppIdentity app, string device, int? pid = 1) =>
        new(CaptureKind.Microphone, app, pid, device, $"Microphone ({device})");

    private static UsageSignal Cam(AppIdentity app, string device) =>
        new(CaptureKind.Camera, app, 1, device, device);

    private static UsageSignal Unknown(AppIdentity app, CaptureKind kind = CaptureKind.Microphone) =>
        new(kind, app, null, string.Empty, string.Empty);

    private static string Device(UsageSession s) => s.DeviceId.Length == 0 ? "(unknown device)" : s.DeviceId;
}
