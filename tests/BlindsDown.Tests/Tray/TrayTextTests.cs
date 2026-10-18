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

    [Fact]
    public void CamerasAreMentionedWhileOneIsActuallyTurnedOff()
    {
        LockStatusInfo locked = new(LockState.Locked, [Mic(), Camera(DeviceProtection.DeviceDisabled)], []);
        LockStatusInfo cameraNotOff = new(LockState.Locked, [Mic(), Camera(DeviceProtection.None)], []);

        Assert.True(TrayText.CamerasLocked(locked));
        Assert.False(TrayText.CamerasLocked(cameraNotOff));
        Assert.Equal("BlindsDown: microphones and cameras locked", TrayText.Tooltip(locked, []));
        Assert.Equal("Microphones and cameras locked", TrayText.Headline(LockState.Locked, TrayText.MentionsCameras(locked, cameraLockOn: true)));

        // Never claims more than is true: camera lock is on, but no camera is turned off.
        Assert.Equal("BlindsDown: microphones locked", TrayText.Tooltip(cameraNotOff, [], cameraLockOn: true));
    }

    [Fact]
    public void WhileUnlockedTheCameraLockSettingDecides()
    {
        Assert.True(TrayText.MentionsCameras(LockStatusInfo.Unlocked, cameraLockOn: true));
        Assert.False(TrayText.MentionsCameras(LockStatusInfo.Unlocked, cameraLockOn: false));
        Assert.Equal("BlindsDown: microphones and cameras unlocked", TrayText.Tooltip(LockStatusInfo.Unlocked, [], cameraLockOn: true));
        Assert.Equal("&Lock microphones and cameras", TrayText.ToggleLabel(LockState.Unlocked, cameras: true));
        Assert.Equal("&Unlock microphones and cameras", TrayText.ToggleLabel(LockState.PartiallyLocked, cameras: true));
    }

    [Theory]
    [InlineData(LockState.Locked, "Microphones and cameras locked")]
    [InlineData(LockState.PartiallyLocked, "Microphones and cameras partially locked")]
    [InlineData(LockState.Locking, "Locking microphones and cameras…")]
    [InlineData(LockState.Unlocking, "Unlocking microphones and cameras…")]
    [InlineData(LockState.Unlocked, "Microphones and cameras unlocked")]
    public void HeadlineWithCameras(LockState state, string expected) =>
        Assert.Equal(expected, TrayText.Headline(state, cameras: true));

    [Fact]
    public void TooltipWithCamerasNeverExceedsTheShellLimit()
    {
        UsageSession[] active =
        [
            .. Enumerable.Range(0, 30).Select(i => Session($"Some Very Long Application Name Number {i}", i % 2 == 0 ? CaptureKind.Microphone : CaptureKind.Camera)),
        ];
        LockStatusInfo status = new(LockState.PartiallyLocked, [Camera(DeviceProtection.DeviceDisabled)], [new string('x', 300), "b"]);

        string tip = TrayText.Tooltip(status, active, cameraLockOn: true);

        Assert.True(tip.Length <= TrayText.MaxTooltipLength, $"{tip.Length} characters");
        Assert.StartsWith("BlindsDown: microphones and cameras partially locked (2 problems)", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemLineSpeaksOfDevicesWhenCamerasAreInvolved()
    {
        Assert.Equal("Some microphones are not protected", TrayText.ProblemMenuLine(new LockStatusInfo(LockState.PartiallyLocked, [Mic()], [])));
        Assert.Equal("Some devices are not protected", TrayText.ProblemMenuLine(new LockStatusInfo(LockState.PartiallyLocked, [Mic(), Camera(DeviceProtection.None)], [])));
    }

    private static DeviceLockInfo Mic() =>
        new("{m}", "Mic", "mic|m", DeviceKind.Microphone, false, false, DeviceProtection.SoftwareMute, null);

    private static DeviceLockInfo Camera(DeviceProtection protection) =>
        new(@"USB\CAM\1", "Camera", "cam|USB\\CAM|CAMERA", DeviceKind.Camera, false, false, protection, null, CameraType.Color);

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
