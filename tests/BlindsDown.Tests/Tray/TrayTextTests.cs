using BlindsDown.Core.Locking;
using BlindsDown.Core.Usage;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

public class TrayTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TooltipStatesTheLockState()
    {
        Assert.Equal("BlindsDown: microphones locked", TrayText.Tooltip(Status(LockState.Locked), []));
        Assert.Equal("BlindsDown: microphones unlocked", TrayText.Tooltip(LockStatusInfo.Unlocked, []));
    }

    [Fact]
    public void TooltipCountsProblemsOfAPartialLock()
    {
        string tip = TrayText.Tooltip(Status(LockState.PartiallyLocked, "Mic A failed", "Mic B failed"), []);
        Assert.Equal("BlindsDown: microphones partially locked (2 problems)", tip);
    }

    [Fact]
    public void TooltipNamesWhoUsesTheMicIncludingTheRelayingApp()
    {
        UsageSession[] active =
        [
            Session("Discord", CaptureKind.Microphone, via: "SteelSeries Sonar"),
            Session("Zoom", CaptureKind.Microphone),
            Session("Zoom", CaptureKind.Microphone), // second device, same app: listed once
            Session("Camera app", CaptureKind.Camera),
            Session("Old call", CaptureKind.Microphone, ended: true),
        ];

        string tip = TrayText.Tooltip(Status(LockState.Unlocked), active);

        Assert.Equal(
            "BlindsDown: microphones unlocked\nMic in use: Discord (via SteelSeries Sonar), Zoom\nCamera in use: Camera app",
            tip);
    }

    [Fact]
    public void TooltipNeverExceedsTheShellLimit()
    {
        UsageSession[] active = [.. Enumerable.Range(0, 30).Select(i => Session($"Some Very Long Application Name Number {i}", CaptureKind.Microphone))];
        string[] problems = [new string('x', 300)];

        string tip = TrayText.Tooltip(Status(LockState.PartiallyLocked, problems), active);

        Assert.True(tip.Length <= TrayText.MaxTooltipLength, $"{tip.Length} characters");
        Assert.StartsWith("BlindsDown: microphones partially locked", tip, StringComparison.Ordinal);
        Assert.EndsWith("…", tip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is too long", 10, "this is t…")]
    [InlineData("trailing space here", 9, "trailing…")]
    [InlineData("abc", 1, "…")]
    [InlineData("abc", 0, "")]
    public void TruncateAddsAnEllipsis(string text, int max, string expected) =>
        Assert.Equal(expected, TrayText.Truncate(text, max));

    [Fact]
    public void TruncateNeverSplitsASurrogatePair()
    {
        string text = "ab\U0001F3A4cdef"; // a microphone emoji is two UTF-16 units
        string cut = TrayText.Truncate(text, 4);
        Assert.Equal("ab…", cut);
    }

    [Theory]
    [InlineData(LockState.Unlocked, "&Lock microphones")]
    [InlineData(LockState.Locked, "&Unlock microphones")]
    [InlineData(LockState.PartiallyLocked, "&Unlock microphones")]
    [InlineData(LockState.Locking, "Locking…")]
    [InlineData(LockState.Unlocking, "Unlocking…")]
    public void ToggleLabelOffersTheOppositeAction(LockState state, string expected) =>
        Assert.Equal(expected, TrayText.ToggleLabel(state));

    [Fact]
    public void ProblemLineShowsTheFirstProblemAndACount()
    {
        Assert.Null(TrayText.ProblemMenuLine(Status(LockState.Locked)));
        Assert.Equal("Mic A failed (+1 more)", TrayText.ProblemMenuLine(Status(LockState.PartiallyLocked, "Mic A failed", "Mic B failed")));
        string longLine = TrayText.ProblemMenuLine(Status(LockState.PartiallyLocked, new string('y', 200), "b"))!;
        Assert.True(longLine.Length <= TrayText.MaxMenuLineLength);
        Assert.EndsWith("(+1 more)", longLine, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageMenuLinesAreShortEnoughForTheMenu()
    {
        UsageSession[] active = [.. Enumerable.Range(0, 10).Select(i => Session($"Application {i}", CaptureKind.Microphone))];
        IReadOnlyList<string> lines = TrayText.UsageMenuLines(active);
        Assert.Single(lines);
        Assert.StartsWith("Microphone in use by Application 0", lines[0], StringComparison.Ordinal);
        Assert.True(lines[0].Length <= TrayText.MaxMenuLineLength);
    }

    private static LockStatusInfo Status(LockState state, params string[] problems) => new(state, [], problems);

    private static UsageSession Session(string app, CaptureKind kind, string? via = null, bool ended = false) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        App = new AppIdentity("exe:" + app, app, null, null),
        DeviceId = "dev",
        DeviceName = "Device",
        ViaApp = via,
        Start = Now.AddMinutes(-5),
        End = ended ? Now : null,
        Sources = ["test"],
    };
}
