using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

public sealed class ExpiringCacheTests
{
    private long _now = 1_000;

    [Fact]
    public void EntriesExpireAfterTheirLifetime()
    {
        var cache = new ExpiringCache<string, int>(TimeSpan.FromSeconds(10), 8, nowMilliseconds: () => _now);
        cache.Set("a", 1);

        _now += 9_999;
        Assert.True(cache.TryGet("a", out int value));
        Assert.Equal(1, value);

        _now += 1;
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SizeIsHardBounded()
    {
        var cache = new ExpiringCache<int, int>(TimeSpan.FromMinutes(5), 4, nowMilliseconds: () => _now);
        for (int i = 0; i < 100; i++)
        {
            cache.Set(i, i);
            Assert.True(cache.Count <= 4);
        }

        Assert.True(cache.TryGet(99, out _));
    }

    [Fact]
    public void ExpiredEntriesAreDroppedBeforeLiveOnesWhenFull()
    {
        var cache = new ExpiringCache<int, int>(TimeSpan.FromSeconds(1), 3, nowMilliseconds: () => _now);
        cache.Set(1, 1);
        cache.Set(2, 2);
        _now += 500;
        cache.Set(3, 3);
        _now += 600;

        cache.Set(4, 4);

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(3, out _));
        Assert.True(cache.TryGet(4, out _));
    }

    [Fact]
    public void ComparerIsHonoured()
    {
        var cache = new ExpiringCache<string, int>(TimeSpan.FromMinutes(1), 4, StringComparer.OrdinalIgnoreCase, () => _now);
        cache.Set(@"C:\App.exe", 7);

        Assert.True(cache.TryGet(@"c:\app.EXE", out int value));
        Assert.Equal(7, value);
    }
}
