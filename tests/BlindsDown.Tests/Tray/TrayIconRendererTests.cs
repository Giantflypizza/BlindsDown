using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Ui;
using BlindsDown.Ui.Tray;
using BlindsLayout = BlindsDown.Ui.TrayIconRenderer.BlindsLayout;

namespace BlindsDown.Tests.Tray;

public partial class TrayIconRendererTests
{
    public static TheoryData<LockState, int, bool> AllStatesAndSizes()
    {
        var data = new TheoryData<LockState, int, bool>();
        foreach (LockState state in Enum.GetValues<LockState>())
        {
            foreach (int size in new[] { 16, 20, 24, 28, 32, 40, 48 })
            {
                data.Add(state, size, true);
                data.Add(state, size, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllStatesAndSizes))]
    public void RenderProducesAnIconOfExactlyTheRequestedSize(LockState state, int size, bool darkTaskbar)
    {
        using Icon icon = TrayIconRenderer.Render(state, size, darkTaskbar);
        Assert.Equal(new Size(size, size), icon.Size);
        Assert.NotEqual(0, icon.Handle);

        using Bitmap bitmap = icon.ToBitmap();
        Assert.Equal(new Size(size, size), bitmap.Size);
        Assert.True(CountVisible(bitmap) > size, "icon should not be empty");
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(8, 16)]
    [InlineData(1000, 256)]
    public void OutOfRangeSizesAreClamped(int requested, int expected)
    {
        using Icon icon = TrayIconRenderer.Render(LockState.Locked, requested, darkTaskbar: true);
        Assert.Equal(expected, icon.Width);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void LockedIsASolidRedBlockWhileUnlockedIsAnOpenWindow(int size)
    {
        int c = size / 2;
        using Bitmap locked = TrayIconRenderer.RenderBitmap(LockState.Locked, size, darkTaskbar: true);
        using Bitmap unlocked = TrayIconRenderer.RenderBitmap(LockState.Unlocked, size, darkTaskbar: true);

        // Shape, not just colour: the middle of the locked icon is filled, the unlocked window is open.
        Color lockedMiddle = MostSaturatedAround(locked, c, c);
        Assert.True(lockedMiddle.A > 200 && lockedMiddle.R > 180 && lockedMiddle.G < 90, $"locked middle was {lockedMiddle}");
        Assert.True(unlocked.GetPixel(c, c + 1).A < 20, "unlocked window should be open (transparent) in the middle");
    }

    [Fact]
    public void TransitionalStateIsHalfDown()
    {
        const int size = 32;
        BlindsLayout layout = BlindsLayout.For(size);
        using Bitmap bitmap = TrayIconRenderer.RenderBitmap(LockState.Locking, size, darkTaskbar: true);
        int x = size / 2;

        // Covered just below the second gap, open in the lower part of the window.
        Assert.True(bitmap.GetPixel(x, layout.GapRows[1] + layout.Gap).A > 200);
        Assert.True(bitmap.GetPixel(x, layout.GapRows[3] - 1).A < 20);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeutralGlyphContrastsWithTheTaskbar(bool darkTaskbar)
    {
        using Bitmap bitmap = TrayIconRenderer.RenderBitmap(LockState.Unlocked, 32, darkTaskbar);
        BlindsLayout layout = BlindsLayout.For(32);
        Color frame = bitmap.GetPixel(layout.Frame.Left, layout.Frame.Top + (layout.Frame.Height / 2));
        Assert.Equal(255, frame.A);
        Assert.Equal(darkTaskbar, frame.GetBrightness() > 0.5f);
    }

    [Fact]
    public void PartiallyLockedHasAmberBodyAndADarkExclamationBadge()
    {
        const int size = 32;
        using Bitmap bitmap = TrayIconRenderer.RenderBitmap(LockState.PartiallyLocked, size, darkTaskbar: true);
        Color body = bitmap.GetPixel(8, 4);
        Assert.Equal(Theme.Warning.ToArgb(), body.ToArgb());

        // Somewhere in the bottom-right quarter there is the dark "!" mark.
        bool foundMark = false;
        for (int y = size / 2; y < size && !foundMark; y++)
        {
            for (int x = size / 2; x < size && !foundMark; x++)
            {
                Color p = bitmap.GetPixel(x, y);
                foundMark = p.A == 255 && p.GetBrightness() < 0.2f;
            }
        }

        Assert.True(foundMark);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(256)]
    public void BlindsLayoutFitsInsideTheFrameOnWholePixels(int size)
    {
        BlindsLayout layout = BlindsLayout.For(size);
        Assert.Equal(4, layout.GapRows.Count);
        Assert.True(layout.Frame.Left >= 1 && layout.Frame.Right <= size - 1);
        Assert.True(layout.Slat >= 1 && layout.Gap >= 1 && layout.Stroke >= 1);
        Assert.True(layout.GapRows[0] - layout.Frame.Top >= 2, "headrail too thin");
        for (int i = 1; i < layout.GapRows.Count; i++)
        {
            Assert.Equal(layout.Gap + layout.Slat, layout.GapRows[i] - layout.GapRows[i - 1]);
        }

        // After the last gap exactly the bottom rail (one stroke) remains.
        Assert.Equal(layout.Frame.Bottom - layout.Stroke, layout.GapRows[^1] + layout.Gap);
    }

    [Fact]
    public void RenderingManyIconsDoesNotLeakHandles()
    {
        nint process = GetCurrentProcess();
        using (TrayIconRenderer.Render(LockState.Locked, 20, true))
        {
            // Warm up GDI+ and caches before counting.
        }

        uint gdiBefore = GetGuiResources(process, 0);
        uint userBefore = GetGuiResources(process, 1);
        for (int i = 0; i < 300; i++)
        {
            using Icon icon = TrayIconRenderer.Render((LockState)(i % 5), 16 + (i % 4 * 8), i % 2 == 0);
            _ = icon.Handle;
        }

        Assert.InRange(GetGuiResources(process, 0), 0u, gdiBefore + 20);
        Assert.InRange(GetGuiResources(process, 1), 0u, userBefore + 20);
    }

    [Fact]
    public void IconBuilderRoundTripsStraightAlpha()
    {
        using var source = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
        Color[] colors = [Color.FromArgb(255, 200, 10, 20), Color.FromArgb(128, 10, 200, 30), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 1, 2, 3), Color.FromArgb(64, 250, 250, 250), Color.FromArgb(255, 255, 255, 255)];
        for (int i = 0; i < colors.Length; i++)
        {
            source.SetPixel(i % 3, i / 3, colors[i]);
        }

        using Icon icon = IconBuilder.FromBitmap(source);
        using Bitmap back = icon.ToBitmap();
        for (int i = 0; i < colors.Length; i++)
        {
            Color actual = back.GetPixel(i % 3, i / 3);
            Assert.Equal(colors[i].A, actual.A);
            if (colors[i].A > 0)
            {
                Assert.InRange(actual.R, colors[i].R - 2, colors[i].R + 2);
                Assert.InRange(actual.G, colors[i].G - 2, colors[i].G + 2);
                Assert.InRange(actual.B, colors[i].B - 2, colors[i].B + 2);
            }
        }
    }

    private static int CountVisible(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>The middle pixel may sit on a light slat gap; take the most saturated pixel nearby.</summary>
    private static Color MostSaturatedAround(Bitmap bitmap, int cx, int cy)
    {
        Color best = bitmap.GetPixel(cx, cy);
        for (int dy = -1; dy <= 1; dy++)
        {
            Color p = bitmap.GetPixel(cx, cy + dy);
            if (p.GetSaturation() > best.GetSaturation())
            {
                best = p;
            }
        }

        return best;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint hProcess, uint uiFlags);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
