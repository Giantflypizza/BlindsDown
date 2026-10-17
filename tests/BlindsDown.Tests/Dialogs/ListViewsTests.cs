using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class ListViewsTests
{
    [Fact]
    public void LastColumnFillsSpareWidth() =>
        Assert.Equal([70, 200, 330], ListViews.Distribute([70, 200, 100], 600, flexibleColumn: -1, flexibleMinimum: 140));

    [Fact]
    public void FlexibleColumnGivesWayWhenTooWide() =>
        Assert.Equal([70, 180, 60, 300], ListViews.Distribute([70, 260, 60, 300], 610, flexibleColumn: 1, flexibleMinimum: 140));

    [Fact]
    public void FlexibleColumnStopsAtItsMinimum() =>
        Assert.Equal([70, 140, 60, 300], ListViews.Distribute([70, 260, 60, 300], 400, flexibleColumn: 1, flexibleMinimum: 140));

    [Fact]
    public void NarrowContentIsNeverWidenedByTheMinimum() =>
        Assert.Equal([70, 90, 340], ListViews.Distribute([70, 90, 100], 500, flexibleColumn: 1, flexibleMinimum: 140));

    [Fact]
    public void FlexibleLastColumnStillFills() =>
        Assert.Equal([100, 400], ListViews.Distribute([100, 300], 500, flexibleColumn: 1, flexibleMinimum: 140));

    [Fact]
    public void NoColumnsIsFine() => Assert.Empty(ListViews.Distribute([], 500, flexibleColumn: 0, flexibleMinimum: 140));
}
