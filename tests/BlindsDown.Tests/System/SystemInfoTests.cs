using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.SystemServices;

public sealed class SystemInfoTests
{
    [Fact]
    public void BootTimeIsNowMinusUptimeRoundedToTheSecond()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, 400, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.Zero), SystemInfo.BootTimeUtc(now, 3_600_000));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 11, 0, 1, TimeSpan.Zero), SystemInfo.BootTimeUtc(now, 3_599_100));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.Zero), SystemInfo.BootTimeUtc(now, 3_599_950));
    }

    [Fact]
    public void BootTimeIsAlwaysUtc()
    {
        var now = new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.FromHours(2));

        DateTimeOffset boot = SystemInfo.BootTimeUtc(now, 60_000);

        Assert.Equal(TimeSpan.Zero, boot.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 11, 59, 0, TimeSpan.Zero), boot);
    }

    [Fact]
    public void RealBootTimeIsInThePastAndStable()
    {
        DateTimeOffset first = SystemInfo.BootTimeUtc();
        DateTimeOffset second = SystemInfo.BootTimeUtc();

        Assert.True(first <= DateTimeOffset.UtcNow);
        Assert.Equal(0, first.UtcTicks % TimeSpan.TicksPerSecond);
        Assert.True((second - first).Duration() <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RemoteSessionCheckDoesNotThrow() => _ = SystemInfo.IsRemoteSession();
}
