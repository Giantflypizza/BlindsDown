using BlindsDown.Core.Helper;

namespace BlindsDown.Tests.App;

/// <summary>The alarm text for lock changes by another program: what really happened, when, and in plain words.</summary>
public sealed class ForeignChangeTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnlockNamesWhatWasTurnedBackOn()
    {
        (string title, string message) = AppHost.ForeignChangeText([Change(HelperChangeKind.Unlocked, ["Front camera"])], beforeStart: false, putBack: false);

        Assert.Equal("Unlocked by another program", title);
        Assert.StartsWith("At ", message, StringComparison.Ordinal);
        Assert.Contains("another program told the camera helper to unlock, so Front camera was turned back on.", message, StringComparison.Ordinal);
        Assert.EndsWith("If that wasn't you, lock again and check which programs are running.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlockWithNothingOffSaysTheLockEndedInsteadOfNamingDevices()
    {
        (_, string message) = AppHost.ForeignChangeText([Change(HelperChangeKind.Unlocked, [])], beforeStart: false, putBack: false);

        Assert.Contains("so the lock ended (none of the devices it covered was off at that moment).", message, StringComparison.Ordinal);
        Assert.DoesNotContain("turned back on", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANarrowingNamesWhatTheLockNoLongerCovers()
    {
        (string title, string message) = AppHost.ForeignChangeText(
            [Change(HelperChangeKind.Narrowed, ["Front camera"], ["Busy camera", "Dock camera"])],
            beforeStart: false,
            putBack: false);

        Assert.Equal("Lock changed by another program", title);
        Assert.Contains("told the camera helper to lock less, so Front camera was turned back on and the lock no longer covers Busy camera and Dock camera.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangesFromWhileBlindsDownWasClosedSaySoAndAreOneAlarm()
    {
        (string title, string message) = AppHost.ForeignChangeText(
            [Change(HelperChangeKind.Unlocked, ["Front camera"]), Change(HelperChangeKind.Narrowed, [], ["Front camera"])],
            beforeStart: true,
            putBack: true);

        Assert.Equal("Unlocked by another program", title); // Even though a lock is in force again.
        Assert.StartsWith("While BlindsDown wasn't running (", message, StringComparison.Ordinal);
        Assert.Contains("other programs changed the camera helper's lock 2 times, so Front camera was turned back on.", message, StringComparison.Ordinal);
        Assert.Contains("BlindsDown has locked again since.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("lock again and", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockByAnotherProgramSaysItMayNotCoverWhatYouChose()
    {
        (string title, string message) = AppHost.ForeignChangeText([Change(HelperChangeKind.Locked, [])], beforeStart: false, putBack: false);

        Assert.Equal("Lock changed by another program", title);
        Assert.Contains("another program set the camera helper's lock, so the lock in force may not cover everything you chose.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockByAnotherProgramThatBlindsDownHasReplacedSaysSoWithoutContradiction()
    {
        (string title, string message) = AppHost.ForeignChangeText([Change(HelperChangeKind.Locked, [])], beforeStart: true, putBack: true);

        Assert.Equal("Lock changed by another program", title);
        Assert.Contains("so for a while the lock in force wasn't the one you chose. BlindsDown has locked again since.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("may not cover", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlockThenALockByAnotherProgramSaysBoth()
    {
        (string title, string message) = AppHost.ForeignChangeText(
            [Change(HelperChangeKind.Unlocked, ["Front camera"]), Change(HelperChangeKind.Locked, [])],
            beforeStart: false,
            putBack: false);

        Assert.Equal("Unlocked by another program", title);
        Assert.Contains("so Front camera was turned back on. Then another program locked again, and its lock may not cover everything you chose.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "no devices")]
    [InlineData(new[] { "A" }, "A")]
    [InlineData(new[] { "A", "B" }, "A and B")]
    [InlineData(new[] { "A", "B", "C" }, "A, B and C")]
    [InlineData(new[] { "A", "B", "C", "D", "E" }, "5 devices (A, B, C, …)")]
    public void DeviceNamesReadLikeASentence(string[] names, string expected) =>
        Assert.Equal(expected, AppHost.DeviceNames(names));

    private static HelperChange Change(HelperChangeKind kind, IReadOnlyList<string> turnedOn, IReadOnlyList<string>? released = null) =>
        new(1, kind, HelperProtocol.DigestOf("other"), "some tool", turnedOn, released ?? [], At);
}
