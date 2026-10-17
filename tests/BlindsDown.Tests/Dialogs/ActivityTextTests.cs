using System.Globalization;
using BlindsDown.Core.Usage;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class ActivityTextTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>UTC+2, like Central European Summer Time, so UTC and local days differ around midnight.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 18, 30, 0, TimeSpan.Zero); // 20:30 local

    [Fact]
    public void DayLabels_UseTodayYesterdayThenWeekdayAndDate()
    {
        var today = new DateOnly(2026, 10, 17);
        Assert.Equal("Today", ActivityText.DayLabel(today, today, Culture));
        Assert.Equal("Yesterday", ActivityText.DayLabel(today.AddDays(-1), today, Culture));
        Assert.Equal("Tuesday, October 13", ActivityText.DayLabel(new DateOnly(2026, 10, 13), today, Culture));
        Assert.Equal("Tuesday, 30 December 2025", ActivityText.DayLabel(new DateOnly(2025, 12, 30), today, Culture));
    }

    [Theory]
    [InlineData(0, "< 1 min")]
    [InlineData(20, "< 1 min")]
    [InlineData(30, "1 min")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData((95 * 60) + 10, "1 h 35 min")]
    [InlineData(-5, "< 1 min")]
    public void Duration_IsShortAndRounded(int seconds, string expected) =>
        Assert.Equal(expected, ActivityText.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(42, "0:42")]
    [InlineData((12 * 60) + 5, "12:05")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-3, "0:00")]
    public void Live_ShowsARunningClock(int seconds, string expected) =>
        Assert.Equal(expected, ActivityText.Live(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Range_IsLocalTimeWithDuration()
    {
        UsageRecord record = Record("Zoom", new DateTimeOffset(2026, 10, 17, 12, 2, 0, TimeSpan.Zero), TimeSpan.FromMinutes(45));
        Assert.Equal("14:02 – 14:47 (45 min)", ActivityText.Range(record, Zone, Culture));
    }

    [Fact]
    public void Range_MarksApproximateTimes()
    {
        UsageRecord record = Record("Zoom", new DateTimeOffset(2026, 10, 17, 12, 2, 0, TimeSpan.Zero), TimeSpan.FromMinutes(5)) with { Approximate = true };
        Assert.Equal("≈ 14:02 – 14:07 (5 min)", ActivityText.Range(record, Zone, Culture));
    }

    [Fact]
    public void Since_AddsTheDayOnlyWhenNotToday()
    {
        Assert.Equal("20:00", ActivityText.Since(Now.AddMinutes(-30), Now, Zone, Culture));
        Assert.Equal("Yesterday 23:50", ActivityText.Since(new DateTimeOffset(2026, 10, 16, 21, 50, 0, TimeSpan.Zero), Now, Zone, Culture));
        Assert.Equal("October 14 09:00", ActivityText.Since(new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero), Now, Zone, Culture));
    }

    [Fact]
    public void App_NamesTheRelayForVirtualDevices()
    {
        Assert.Equal("Discord (via SteelSeries Sonar)", ActivityText.App("Discord", "SteelSeries Sonar"));
        Assert.Equal("Discord", ActivityText.App("Discord", null));
        Assert.Equal("Discord", ActivityText.App("Discord", " "));
    }

    [Fact]
    public void Device_HasAFallbackWhenTheSourceCannotTell()
    {
        Assert.Equal("A microphone", ActivityText.Device(CaptureKind.Microphone, string.Empty));
        Assert.Equal("A camera", ActivityText.Device(CaptureKind.Camera, null));
        Assert.Equal("Headset", ActivityText.Device(CaptureKind.Microphone, "Headset"));
    }

    [Fact]
    public void Sentence_ReadsNaturally()
    {
        UsageRecord record = Record("Discord", new DateTimeOffset(2026, 10, 17, 12, 2, 0, TimeSpan.Zero), TimeSpan.FromMinutes(45)) with { ViaApp = "SteelSeries Sonar" };
        Assert.Equal(
            "Discord used your microphone (Mic) 14:02 – 14:47 (45 min) via SteelSeries Sonar.",
            ActivityText.Sentence(record, Zone, Culture));
    }

    [Fact]
    public void GroupByDay_UsesLocalDaysNewestFirst()
    {
        // 22:30 UTC on the 16th is 00:30 local on the 17th: it belongs to "Today".
        UsageRecord lateUtc = Record("Zoom", new DateTimeOffset(2026, 10, 16, 22, 30, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10));
        UsageRecord yesterday = Record("Teams", new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10));
        UsageRecord today = Record("Discord", new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10));

        List<HistoryDay> days = ActivityText.GroupByDay([yesterday, lateUtc, today], Zone, Now, Culture);

        Assert.Equal(["Today", "Yesterday"], days.Select(d => d.Label));
        Assert.Equal(["Discord", "Zoom"], days[0].Records.Select(r => r.AppName));
        Assert.Equal(["Teams"], days[1].Records.Select(r => r.AppName));
    }

    [Fact]
    public void GroupByDay_MergesShortGapsOfTheSameAppAndDevice()
    {
        DateTimeOffset start = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        UsageRecord first = Record("Zoom", start, TimeSpan.FromMinutes(10));
        UsageRecord second = Record("Zoom", start.AddMinutes(10).AddSeconds(10), TimeSpan.FromMinutes(5)) with { Approximate = true };
        UsageRecord later = Record("Zoom", start.AddMinutes(30), TimeSpan.FromMinutes(5));

        HistoryDay day = Assert.Single(ActivityText.GroupByDay([first, second, later], Zone, Now, Culture));

        Assert.Equal(2, day.Records.Count);
        UsageRecord merged = day.Records[1];
        Assert.Equal(start, merged.Start);
        Assert.Equal(start.AddMinutes(15).AddSeconds(10), merged.End);
        Assert.True(merged.Approximate);
    }

    [Fact]
    public void IdentityOf_RebuildsTheGroupKeyOfStoreApps()
    {
        var app = new AppIdentity("pkg-key", "Sound Recorder", @"C:\Program Files\WindowsApps\rec.exe", "Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe");
        UsageRecord record = UsageRecord.From(Session(app), Now);

        Assert.Equal(app.GroupKey, ActivityText.IdentityOf(record).GroupKey);
    }

    [Fact]
    public void IdentityOf_RebuildsTheGroupKeyOfDesktopApps()
    {
        var app = new AppIdentity(@"c:\users\me\appdata\local\discord\app-1.0.9262\discord.exe", "Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0.9262\Discord.exe", null);
        UsageRecord record = UsageRecord.From(Session(app), Now);

        AppIdentity rebuilt = ActivityText.IdentityOf(record);

        Assert.Equal(app.GroupKey, rebuilt.GroupKey);
        Assert.Equal("Discord", rebuilt.DisplayName);
    }

    [Fact]
    public void IdentityOf_RebuildsTheGroupKeyWithoutAnExePath()
    {
        var app = new AppIdentity("svchost:audiosrv", "Windows Audio", null, null);
        UsageRecord record = UsageRecord.From(Session(app), Now);

        Assert.Equal(app.GroupKey, ActivityText.IdentityOf(record).GroupKey);
        Assert.Equal(AppIdentity.Unknown.GroupKey, ActivityText.IdentityOf(UsageRecord.From(Session(AppIdentity.Unknown), Now)).GroupKey);
    }

    private static UsageRecord Record(string app, DateTimeOffset start, TimeSpan duration) => new()
    {
        Kind = CaptureKind.Microphone,
        AppKey = "exe:" + app.ToUpperInvariant(),
        AppName = app,
        DeviceName = "Mic",
        Start = start,
        End = start + duration,
    };

    private static UsageSession Session(AppIdentity app) => new()
    {
        Id = Guid.NewGuid(),
        Kind = CaptureKind.Microphone,
        App = app,
        DeviceId = "{0.0.1.00000000}.{x}",
        DeviceName = "Mic",
        Start = Now.AddMinutes(-5),
        Sources = ["test"],
    };
}
