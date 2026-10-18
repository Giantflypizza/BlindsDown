using System.Drawing;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Ui;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

public class OverlayTests
{
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1032); // 1080p with a 48 px taskbar at the bottom
    private static readonly Size Pill = new(120, 28);

    [Theory]
    [InlineData(OverlayCorner.TopRight, 1920 - 12 - 120, 12)]
    [InlineData(OverlayCorner.TopLeft, 12, 12)]
    [InlineData(OverlayCorner.BottomRight, 1920 - 12 - 120, 1032 - 12 - 28)]
    [InlineData(OverlayCorner.BottomLeft, 12, 1032 - 12 - 28)]
    public void PillSitsInTheChosenCornerOfTheWorkingArea(OverlayCorner corner, int x, int y)
    {
        Rectangle placed = OverlayLayout.PlaceInCorner(WorkArea, Pill, corner, 12);
        Assert.Equal(new Rectangle(x, y, Pill.Width, Pill.Height), placed);
    }

    [Fact]
    public void PillAvoidsATaskbarAtTheTopOfASecondaryMonitorLeftOfThePrimary()
    {
        var work = new Rectangle(-2560, 60, 2560, 1380); // taskbar on top, monitor at negative coordinates
        Rectangle placed = OverlayLayout.PlaceInCorner(work, Pill, OverlayCorner.TopLeft, 15);
        Assert.Equal(new Point(-2545, 75), placed.Location);
        Assert.True(work.Contains(placed));
    }

    [Fact]
    public void PillStaysInsideATinyWorkingArea()
    {
        var work = new Rectangle(100, 100, 80, 20);
        Rectangle placed = OverlayLayout.PlaceInCorner(work, Pill, OverlayCorner.BottomRight, 12);
        Assert.Equal(new Point(100, 100), placed.Location);
    }

    [Fact]
    public void EdgeStripsFrameTheWholeMonitorWithoutOverlapping()
    {
        var bounds = new Rectangle(1920, -200, 2560, 1440);
        Rectangle[] strips = OverlayLayout.EdgeStrips(bounds, 5);

        Assert.Equal(4, strips.Length);
        Assert.Equal(new Rectangle(1920, -200, 2560, 5), strips[0]);
        Assert.Equal(new Rectangle(1920, 1235, 2560, 5), strips[1]);
        Assert.Equal(new Rectangle(1920, -195, 5, 1430), strips[2]);
        Assert.Equal(new Rectangle(4475, -195, 5, 1430), strips[3]);
        for (int i = 0; i < strips.Length; i++)
        {
            Assert.True(bounds.Contains(strips[i]));
            for (int j = i + 1; j < strips.Length; j++)
            {
                Assert.False(strips[i].IntersectsWith(strips[j]), $"strips {i} and {j} overlap");
            }
        }
    }

    [Theory]
    [InlineData(4, 1f, 4)]
    [InlineData(4, 1.25f, 5)]
    [InlineData(4, 1.5f, 6)]
    [InlineData(4, 1.75f, 7)]
    [InlineData(4, 2f, 8)]
    [InlineData(12, 1.25f, 15)]
    [InlineData(0.2f, 1f, 1)]
    public void ScaleRoundsToWholePixels(float logical, float scale, int expected) =>
        Assert.Equal(expected, OverlayLayout.Scale(logical, scale));

    [Fact]
    public void CenterInCentresAndNeverStartsOutside()
    {
        Assert.Equal(new Rectangle(820, 456, 280, 120), OverlayLayout.CenterIn(WorkArea, new Size(280, 120)));
        var small = new Rectangle(10, 10, 100, 50);
        Assert.Equal(new Point(10, 10), OverlayLayout.CenterIn(small, new Size(280, 120)).Location);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void PillBitmapHasTransparentCornersAndAnOpaqueStateColouredBody(float scale)
    {
        using Bitmap bitmap = OverlayPainter.RenderPill(LockState.Locked, scale, out int padding);
        Size body = OverlayPainter.MeasurePill(LockState.Locked, scale);

        Assert.Equal(body.Width + (2 * padding), bitmap.Width);
        Assert.Equal(body.Height + (2 * padding), bitmap.Height);
        Assert.Equal(OverlayLayout.Scale(28, scale), body.Height);

        // Outside the shadow nothing is drawn, and the body's bounding-box corner is outside the round end.
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.True(bitmap.GetPixel(padding, padding).A < 128);

        // Inside the right round end (clear of the text) the pill is solid state red.
        Color fill = bitmap.GetPixel(padding + body.Width - OverlayLayout.Scale(5, scale), padding + (body.Height / 2));
        Assert.Equal(Theme.Locked.ToArgb(), fill.ToArgb());
    }

    [Fact]
    public void PillGrowsWithScaleAndText()
    {
        Size small = OverlayPainter.MeasurePill(LockState.Locked, 1f);
        Size large = OverlayPainter.MeasurePill(LockState.Locked, 2f);
        Size partial = OverlayPainter.MeasurePill(LockState.PartiallyLocked, 1f);

        // Roughly twice as wide: not exactly, because small text is grid-fitted (drawn a little narrower) and the
        // pill follows the text as drawn.
        Assert.InRange(large.Width, (small.Width * 2) - 6, (small.Width * 2) + 14);
        Assert.True(partial.Width > small.Width);
    }

    [Fact]
    public void OsdIsAtLeastTheDesignSizeAndWrapsLongDetailsInsteadOfGrowingForever()
    {
        Size normal = OverlayPainter.MeasureOsd(LockState.Locked, null, 1f);
        Assert.True(normal.Width >= 280 && normal.Height >= 120, normal.ToString());

        Size scaled = OverlayPainter.MeasureOsd(LockState.Locked, null, 1.5f);
        Assert.True(scaled.Width >= 420 && scaled.Height >= 180, scaled.ToString());

        string longDetail = string.Join(' ', Enumerable.Repeat("Realtek microphone could not be muted.", 6));
        Size wrapped = OverlayPainter.MeasureOsd(LockState.PartiallyLocked, longDetail, 1f);
        Assert.True(wrapped.Width <= 460, wrapped.ToString());
        Assert.True(wrapped.Height > normal.Height, wrapped.ToString());
    }

    [Fact]
    public void OsdUsesDefaultDetailsUnlessGivenOne()
    {
        Assert.Equal("Every microphone is muted", OverlayPainter.OsdDetail(LockState.Locked, null));
        Assert.Equal("Every microphone is muted", OverlayPainter.OsdDetail(LockState.Locked, "  "));
        Assert.Equal("Zoom holds the mic", OverlayPainter.OsdDetail(LockState.PartiallyLocked, "Zoom holds the mic"));
        Assert.Equal("Microphones unlocked", OverlayPainter.OsdTitle(LockState.Unlocked));
        Assert.Equal("PARTIALLY LOCKED", OverlayPainter.PillText(LockState.PartiallyLocked));
        Assert.Equal("MIC LOCKED", OverlayPainter.PillText(LockState.Locked));
    }

    [Fact]
    public void PillAndFlashSayCamerasWhenTheyAreLocked()
    {
        Assert.Equal("MIC + CAMERA LOCKED", OverlayPainter.PillText(LockState.Locked, cameras: true));
        Assert.Equal("PARTIALLY LOCKED", OverlayPainter.PillText(LockState.PartiallyLocked, cameras: true));
        Assert.Equal("Microphones and cameras locked", OverlayPainter.OsdTitle(LockState.Locked, cameras: true));
        Assert.Equal("Microphones and cameras unlocked", OverlayPainter.OsdTitle(LockState.Unlocked, cameras: true));
        Assert.Equal("Every microphone is muted and every camera is off", OverlayPainter.OsdDetail(LockState.Locked, null, cameras: true));
        Assert.True(OverlayPainter.MeasurePill(LockState.Locked, 1f, cameras: true).Width > OverlayPainter.MeasurePill(LockState.Locked, 1f).Width);
    }

    /// <summary>
    /// The pill is sized to its text at every scale: the text (white ink) never reaches into the right-hand
    /// padding, and no space is wasted either (the text ends where the padding starts, within a pixel or two).
    /// GDI+ draws small grid-fitted text narrower than it measures it, so this failed at 100 % before the pill
    /// measured its drawn text.
    /// </summary>
    [Theory]
    [InlineData(1f, false)]
    [InlineData(1f, true)]
    [InlineData(1.25f, true)]
    [InlineData(1.5f, true)]
    [InlineData(1.75f, true)]
    [InlineData(2f, true)]
    [InlineData(2.5f, true)]
    [InlineData(3f, true)]
    public void PillFitsItsTextAtEveryScale(float scale, bool cameras)
    {
        using Bitmap bitmap = OverlayPainter.RenderPill(LockState.Locked, scale, out int padding, cameras);
        Size body = OverlayPainter.MeasurePill(LockState.Locked, scale, cameras);

        int rightmostInk = -1;
        for (int x = padding; x < padding + body.Width; x++)
        {
            for (int y = padding + (body.Height / 4); y < padding + (body.Height * 3 / 4); y++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 255 && pixel.G > 110 && pixel.B > 110)
                {
                    rightmostInk = Math.Max(rightmostInk, x);
                }
            }
        }

        int textEndsBefore = padding + body.Width - OverlayLayout.Scale(13f, scale); // the right padding
        Assert.InRange(rightmostInk, textEndsBefore - 3, textEndsBefore + 1);
    }

    [Fact]
    public void AmberUsesDarkInkForContrast()
    {
        Assert.Equal(Theme.LightText, OverlayPainter.OnStateColor(LockState.PartiallyLocked));
        Assert.Equal(Color.White, OverlayPainter.OnStateColor(LockState.Locked));
    }

    [Fact]
    public void OsdFadeStartsTranslucentEndsInvisibleAndNeverReachesFullOpacity()
    {
        Assert.Equal(OsdWindow.MaxOpacity, OsdFlash.FadeOpacity(0));
        Assert.Equal(0, OsdFlash.FadeOpacity(1));
        Assert.Equal(0, OsdFlash.FadeOpacity(2));
        Assert.Equal(OsdWindow.MaxOpacity, OsdFlash.FadeOpacity(-1));
        double previous = double.MaxValue;
        for (double t = 0; t <= 1; t += 0.05)
        {
            double opacity = OsdFlash.FadeOpacity(t);
            Assert.True(opacity < 1 && opacity <= previous);
            previous = opacity;
        }
    }

    [Fact]
    public void OverlayWindowIsHiddenFromCaptureAndNeverActivates()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            // The window is created but never shown.
            using var hidden = new OverlayWindow(new Rectangle(-32000, -32000, 40, 20), hideFromCapture: true);
            using var visibleInShares = new OverlayWindow(new Rectangle(-32000, -32000, 40, 20), hideFromCapture: false);
            _ = hidden.Handle;
            _ = visibleInShares.Handle;

            Assert.True(hidden.IsExcludedFromCapture);
            Assert.False(visibleInShares.IsExcludedFromCapture);
            Assert.False(hidden.Visible);

            using Bitmap content = OverlayPainter.RenderPill(LockState.Locked, 1f, out _);
            Assert.True(hidden.Present(content, 255));
            Assert.False(hidden.Visible);
        });
    }
}
