using BlindsDown.Core.Time;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class AlertLogicTests
{
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size Pill = new(80, 26);

    [Fact]
    public void Coalescer_SuppressesTheSameAlertWithinTheWindow()
    {
        var clock = new ManualClock();
        var coalescer = new AlertCoalescer(clock, TimeSpan.FromSeconds(3));

        Assert.False(coalescer.IsRepeat("Locked", "All muted"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(coalescer.IsRepeat("Locked", "All muted"));
        Assert.False(coalescer.IsRepeat("Locked", "Something else"));
        Assert.False(coalescer.IsRepeat("Other", "All muted"));
    }

    [Fact]
    public void Coalescer_ShowsTheAlertAgainAfterAQuietWindow()
    {
        var clock = new ManualClock();
        var coalescer = new AlertCoalescer(clock, TimeSpan.FromSeconds(3));

        Assert.False(coalescer.IsRepeat("Locked", "All muted"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(coalescer.IsRepeat("Locked", "All muted"));
    }

    [Fact]
    public void Coalescer_WindowSlidesWhileABurstContinues()
    {
        var clock = new ManualClock();
        var coalescer = new AlertCoalescer(clock, TimeSpan.FromSeconds(3));

        Assert.False(coalescer.IsRepeat("Unmuted", "Mic"));
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(coalescer.IsRepeat("Unmuted", "Mic"));
        }
    }

    [Fact]
    public void Coalescer_KeyDoesNotConfuseTitleAndMessageBoundaries() =>
        Assert.NotEqual(AlertCoalescer.KeyOf("a b", "c"), AlertCoalescer.KeyOf("a", "b c"));

    [Fact]
    public void Stack_IsEmptyWithoutPopups()
    {
        AlertStackPlacement placement = AlertStackLayout.Compute(WorkArea, [], Pill, 12, 8);

        Assert.Empty(placement.Locations);
        Assert.Equal(0, placement.HiddenCount);
        Assert.Null(placement.PillLocation);
    }

    [Fact]
    public void Stack_PutsTheNewestInTheCornerAndOlderOnesAbove()
    {
        AlertStackPlacement placement = AlertStackLayout.Compute(WorkArea, [new Size(400, 100), new Size(400, 80), new Size(400, 120)], Pill, 12, 8);

        Assert.Equal(new Point(1920 - 12 - 400, 1040 - 12 - 100), placement.Locations[0]);
        Assert.Equal(new Point(1508, 928 - 8 - 80), placement.Locations[1]);
        Assert.Equal(new Point(1508, 840 - 8 - 120), placement.Locations[2]);
        Assert.Equal(0, placement.HiddenCount);
        Assert.Null(placement.PillLocation);
    }

    [Fact]
    public void Stack_ShowsAtMostFourAndCollapsesTheRestIntoThePill()
    {
        Size[] sizes = Enumerable.Repeat(new Size(400, 100), 6).ToArray();

        AlertStackPlacement placement = AlertStackLayout.Compute(WorkArea, sizes, Pill, 12, 8);

        Assert.Equal(4, placement.Locations.Count(l => l is not null));
        Assert.All(placement.Locations.Take(4), l => Assert.NotNull(l));
        Assert.All(placement.Locations.Skip(4), l => Assert.Null(l));
        Assert.Equal(2, placement.HiddenCount);
        Point topmost = placement.Locations[3]!.Value;
        Assert.Equal(new Point(1920 - 12 - 80, topmost.Y - 8 - 26), placement.PillLocation);
    }

    [Fact]
    public void Stack_ShowsFewerWhenTheScreenIsShortAndKeepsRoomForThePill()
    {
        var shortArea = new Rectangle(0, 0, 1280, 400);
        Size[] sizes = Enumerable.Repeat(new Size(400, 110), 4).ToArray();

        AlertStackPlacement placement = AlertStackLayout.Compute(shortArea, sizes, Pill, 12, 8);

        int visible = placement.Locations.Count(l => l is not null);
        Assert.Equal(2, visible);
        Assert.Equal(2, placement.HiddenCount);
        Assert.NotNull(placement.PillLocation);
        Assert.True(placement.PillLocation!.Value.Y >= shortArea.Top + 12);
        Assert.All(placement.Locations.Where(l => l is not null), l => Assert.True(l!.Value.Y >= shortArea.Top + 12));
    }

    [Fact]
    public void Stack_AlwaysShowsTheNewestEvenIfItIsTallerThanTheScreen()
    {
        var tiny = new Rectangle(0, 0, 800, 200);

        AlertStackPlacement placement = AlertStackLayout.Compute(tiny, [new Size(400, 300), new Size(400, 50)], Pill, 12, 8);

        Assert.Equal(new Point(800 - 12 - 400, 0), placement.Locations[0]);
        Assert.Null(placement.Locations[1]);
        Assert.Equal(1, placement.HiddenCount);
    }

    [Fact]
    public void Stack_RespectsAWorkAreaThatDoesNotStartAtZero()
    {
        // Taskbar on the left/top: the work area is offset.
        var offset = new Rectangle(60, 48, 1860, 1032);

        AlertStackPlacement placement = AlertStackLayout.Compute(offset, [new Size(400, 100)], Pill, 12, 8);

        Assert.Equal(new Point(offset.Right - 12 - 400, offset.Bottom - 12 - 100), placement.Locations[0]);
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
