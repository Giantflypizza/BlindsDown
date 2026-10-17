using BlindsDown.Core.Time;

namespace BlindsDown.Tests.Core.Fakes;

/// <summary>A clock that only moves when a test moves it, so cooldowns and grace periods are deterministic.</summary>
internal sealed class FakeClock : IClock
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = Epoch;

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
