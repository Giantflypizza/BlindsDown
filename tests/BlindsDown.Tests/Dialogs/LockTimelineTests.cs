using System.Diagnostics;
using System.Globalization;
using BlindsDown.Core.Usage;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class LockTimelineTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>UTC+2, so the shown times differ from the stored UTC ones.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static readonly DateTimeOffset Noon = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero); // 12:00 local

    [Fact]
    public void Pair_JoinsEachLockWithTheUnlockAfterIt_NewestFirst()
    {
        List<LockPeriod> periods = LockTimeline.Pair(
        [
            // Newest first, as the log returns them.
            Unlock(Noon.AddHours(3), LockEventSource.Tray, "All 5 devices restored exactly as before."),
            Lock(Noon.AddHours(2), LockEventSource.Automatic),
            Unlock(Noon.AddMinutes(45), LockEventSource.Hotkey),
            Lock(Noon, LockEventSource.Hotkey, "4 microphones, 1 camera"),
        ]);

        Assert.Equal(2, periods.Count);
        Assert.Equal(new LockPeriod(Noon.AddHours(2), Noon.AddHours(3), LockEventSource.Automatic, LockEventSource.Tray, null, "All 5 devices restored exactly as before.", false, false), periods[0]);
        Assert.Equal(new LockPeriod(Noon, Noon.AddMinutes(45), LockEventSource.Hotkey, LockEventSource.Hotkey, "4 microphones, 1 camera", null, false, false), periods[1]);
    }

    [Fact]
    public void Pair_DoesNotDependOnTheOrderOfTheLog()
    {
        LockEventRecord[] events =
        [
            Lock(Noon, LockEventSource.Hotkey),
            Unlock(Noon.AddMinutes(10), LockEventSource.Tray),
            Lock(Noon.AddMinutes(20), LockEventSource.Tray),
            Unlock(Noon.AddMinutes(30), LockEventSource.Hotkey),
        ];

        Assert.Equal(LockTimeline.Pair(events), LockTimeline.Pair(events.Reverse()));
        Assert.Equal(LockTimeline.Pair(events), LockTimeline.Pair([events[2], events[0], events[3], events[1]]));
    }

    [Fact]
    public void Pair_TheLastLockWithoutAnUnlockIsStillOn()
    {
        LockPeriod period = Assert.Single(LockTimeline.Pair([Lock(Noon, LockEventSource.Tray)]));

        Assert.True(period.Ongoing);
        Assert.Null(period.End);
        Assert.Equal("12:00 – now", LockTimeline.Range(period, Zone, Culture));
    }

    [Fact]
    public void Pair_TwoLocksInARowMeanTheFirstUnlockWasNotRecorded()
    {
        List<LockPeriod> periods = LockTimeline.Pair([Lock(Noon, LockEventSource.Hotkey), Lock(Noon.AddHours(1), LockEventSource.Recovery)]);

        Assert.Equal(2, periods.Count);
        Assert.True(periods[0].Ongoing);
        Assert.False(periods[1].Ongoing);
        Assert.Null(periods[1].End);
        Assert.Equal("12:00, unlock not recorded", LockTimeline.Range(periods[1], Zone, Culture));
        Assert.EndsWith("No unlock was recorded after it.", LockTimeline.Sentence(periods[1], Zone, Culture), StringComparison.Ordinal);
    }

    [Fact]
    public void Pair_AnUnlockWhoseLockIsOlderThanTheHistoryStandsAlone()
    {
        LockPeriod period = Assert.Single(LockTimeline.Pair([Unlock(Noon, LockEventSource.Recovery, "1 microphone restored exactly as before.")]));

        Assert.Null(period.Start);
        Assert.Equal(Noon, period.At);
        Assert.Equal("Unlocked · startup", LockTimeline.Title(period));
        Assert.Equal("12:00", LockTimeline.Range(period, Zone, Culture));
        Assert.Equal("Unlocked at startup at 12:00. Unlocked: 1 microphone restored exactly as before.", LockTimeline.Sentence(period, Zone, Culture));
    }

    [Fact]
    public void Pair_AProblemAtEitherEndFlagsThePeriod()
    {
        Assert.True(Assert.Single(LockTimeline.Pair([Lock(Noon, LockEventSource.Hotkey, problems: true), Unlock(Noon.AddMinutes(1), LockEventSource.Hotkey)])).HadProblems);
        Assert.True(Assert.Single(LockTimeline.Pair([Lock(Noon, LockEventSource.Hotkey), Unlock(Noon.AddMinutes(1), LockEventSource.Hotkey, problems: true)])).HadProblems);
        Assert.False(Assert.Single(LockTimeline.Pair([Lock(Noon, LockEventSource.Hotkey), Unlock(Noon.AddMinutes(1), LockEventSource.Hotkey)])).HadProblems);
    }

    [Fact]
    public void Title_SaysHowItWasLockedAndUnlocked()
    {
        Assert.Equal("Locked · hotkey / tray", LockTimeline.Title(Period(LockEventSource.Hotkey, LockEventSource.Tray)));
        Assert.Equal("Locked · hotkey", LockTimeline.Title(Period(LockEventSource.Hotkey, LockEventSource.Hotkey)));
        Assert.Equal("Locked · automatic / other program", LockTimeline.Title(Period(LockEventSource.Automatic, LockEventSource.OtherProgram)));
        Assert.Equal("Locked · tray", LockTimeline.Title(Period(LockEventSource.Tray, null)));
        Assert.Equal("⚠ Locked · hotkey / exit", LockTimeline.Title(Period(LockEventSource.Hotkey, LockEventSource.Exit) with { HadProblems = true }));
    }

    [Theory]
    [InlineData(LockEventSource.Hotkey, "hotkey")]
    [InlineData(LockEventSource.Tray, "tray")]
    [InlineData(LockEventSource.OtherProgram, "other program")]
    [InlineData(LockEventSource.Automatic, "automatic")]
    [InlineData(LockEventSource.Recovery, "startup")]
    [InlineData(LockEventSource.Exit, "exit")]
    public void Source_HasAShortWordForEverySource(LockEventSource source, string expected) =>
        Assert.Equal(expected, LockTimeline.Source(source));

    [Fact]
    public void Range_IsLocalTimeWithDuration()
    {
        LockPeriod period = Period(LockEventSource.Hotkey, LockEventSource.Tray);
        Assert.Equal("12:00 – 12:45 (45 min)", LockTimeline.Range(period, Zone, Culture));
    }

    [Fact]
    public void Sentence_TellsTheWholeStoryWithBothSummaries()
    {
        LockPeriod period = Period(LockEventSource.Hotkey, LockEventSource.OtherProgram) with
        {
            LockSummary = "4 microphones, 1 camera",
            UnlockSummary = "4 restored exactly, 1 needs attention.",
            HadProblems = true,
        };

        Assert.Equal(
            "Locked with the hotkey at 12:00, unlocked by another program at 12:45 (45 min). Locked: 4 microphones, 1 camera. "
                + "Unlocked: 4 restored exactly, 1 needs attention. Not everything went as planned.",
            LockTimeline.Sentence(period, Zone, Culture));
        Assert.Equal("4 microphones, 1 camera", LockTimeline.Devices(period));
    }

    [Fact]
    public void Sentence_ForALockThatIsStillOn()
    {
        LockPeriod period = Period(LockEventSource.Automatic, null) with { End = null, Ongoing = true };
        Assert.Equal("Locked automatically at 12:00. Still locked.", LockTimeline.Sentence(period, Zone, Culture));
        Assert.Equal(string.Empty, LockTimeline.Devices(period));
    }

    [Fact]
    public void GroupByDay_MergesLockPeriodsWithAppUseByTime()
    {
        UsageRecord zoom = new()
        {
            Kind = CaptureKind.Microphone,
            AppKey = "exe:ZOOM",
            AppName = "Zoom",
            DeviceName = "Mic",
            Start = Noon.AddMinutes(50),
            End = Noon.AddMinutes(55),
        };
        List<LockPeriod> locks = LockTimeline.Pair(
        [
            Lock(Noon, LockEventSource.Hotkey),
            Unlock(Noon.AddMinutes(45), LockEventSource.Hotkey),
            Lock(Noon.AddHours(-20), LockEventSource.Tray), // yesterday, 16:00 local
            Unlock(Noon.AddHours(-19), LockEventSource.Tray),
        ]);

        List<HistoryDay> days = ActivityText.GroupByDay([zoom], Zone, Noon.AddHours(1), Culture, locks);

        Assert.Equal(["Today", "Yesterday"], days.Select(d => d.Label));
        Assert.Equal([Noon.AddMinutes(50), Noon], days[0].Entries.Select(e => e.At));
        Assert.Same(zoom, days[0].Entries[0].Record);
        Assert.NotNull(days[0].Entries[1].Lock);
        Assert.Equal([zoom], days[0].Records);
        Assert.Empty(days[1].Records);
        Assert.Single(days[1].Entries);
    }

    [Fact]
    public void Pairing_AndGrouping_StayFastWithAYearOfLocks()
    {
        var events = new List<LockEventRecord>();
        for (int i = 0; i < 5000; i++)
        {
            DateTimeOffset start = Noon.AddHours(-i * 2);
            events.Add(Unlock(start.AddMinutes(30), LockEventSource.Tray));
            events.Add(Lock(start, LockEventSource.Hotkey));
        }

        var clock = Stopwatch.StartNew();
        List<LockPeriod> periods = LockTimeline.Pair(events);
        List<HistoryDay> days = ActivityText.GroupByDay([], Zone, Noon, Culture, periods);
        clock.Stop();

        Assert.Equal(5000, periods.Count);
        Assert.Equal(5000, days.Sum(d => d.Entries.Count));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }

    private static LockPeriod Period(LockEventSource locked, LockEventSource? unlocked) =>
        new(Noon, Noon.AddMinutes(45), locked, unlocked, null, null, HadProblems: false, Ongoing: false);

    [Fact]
    public void Pair_AnUpdateChangesTheOpenLocksSummaryAndKeepsItsProblems()
    {
        // The microphones stay locked while another program unlocks the cameras, and the cameras are locked again later.
        List<LockPeriod> periods = LockTimeline.Pair(
        [
            Lock(Noon, LockEventSource.Hotkey, "2 microphones and 1 camera locked"),
            Update(Noon.AddMinutes(5), LockEventSource.OtherProgram, "2 microphones locked. Unlocked by another program", problems: true),
            Update(Noon.AddMinutes(6), LockEventSource.Tray, "2 microphones and 1 camera locked (locked again after another program changed the lock)"),
            Unlock(Noon.AddMinutes(30), LockEventSource.Hotkey),
        ]);

        LockPeriod period = Assert.Single(periods);
        Assert.Equal((Noon, Noon.AddMinutes(30)), (period.Start, period.End));
        Assert.Equal(LockEventSource.Hotkey, period.LockedBy); // Still the user's lock, not "locked by another program".
        Assert.Equal("2 microphones and 1 camera locked (locked again after another program changed the lock)", period.LockSummary);
        Assert.True(period.HadProblems);
    }

    [Fact]
    public void Pair_AnUpdateWithoutAnOpenLockIsLeftOut()
    {
        List<LockPeriod> periods = LockTimeline.Pair([Update(Noon, LockEventSource.OtherProgram, "Lock changed by another program", problems: true)]);

        Assert.Empty(periods);
    }

    private static LockEventRecord Update(DateTimeOffset at, LockEventSource source, string? summary = null, bool problems = false) =>
        new() { At = at, Action = LockEventAction.Update, Source = source, Summary = summary, HadProblems = problems };

    private static LockEventRecord Lock(DateTimeOffset at, LockEventSource source, string? summary = null, bool problems = false) =>
        new() { At = at, Action = LockEventAction.Lock, Source = source, Summary = summary, HadProblems = problems };

    private static LockEventRecord Unlock(DateTimeOffset at, LockEventSource source, string? summary = null, bool problems = false) =>
        new() { At = at, Action = LockEventAction.Unlock, Source = source, Summary = summary, HadProblems = problems };
}
