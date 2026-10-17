using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

public class TrayClickFilterTests
{
    private const int DoubleClickMs = 500;

    [Fact]
    public void SingleClickToggles()
    {
        var filter = new TrayClickFilter(DoubleClickMs);
        Assert.True(filter.OnLeftMouseUp(1_000));
    }

    [Fact]
    public void DoubleClickTogglesOnlyOnce()
    {
        // The shell sends: down, up, DBLCLK, up.
        var filter = new TrayClickFilter(DoubleClickMs);
        Assert.True(filter.OnLeftMouseUp(1_000));
        filter.OnDoubleClick();
        Assert.False(filter.OnLeftMouseUp(1_150));

        // The swallow flag is used up: a later, separate click works again.
        Assert.True(filter.OnLeftMouseUp(3_000));
    }

    [Fact]
    public void QuickSecondUpWithoutDoubleClickMessageIsIgnored()
    {
        var filter = new TrayClickFilter(DoubleClickMs);
        Assert.True(filter.OnLeftMouseUp(1_000));
        Assert.False(filter.OnLeftMouseUp(1_200));
    }

    [Fact]
    public void SeparateClicksBothToggle()
    {
        var filter = new TrayClickFilter(DoubleClickMs);
        Assert.True(filter.OnLeftMouseUp(1_000));
        Assert.True(filter.OnLeftMouseUp(1_000 + DoubleClickMs));
    }

    [Fact]
    public void ClockGoingBackwardsDoesNotBlockClicks()
    {
        var filter = new TrayClickFilter(DoubleClickMs);
        Assert.True(filter.OnLeftMouseUp(10_000));
        Assert.True(filter.OnLeftMouseUp(5_000));
    }
}
