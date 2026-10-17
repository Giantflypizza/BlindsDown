namespace BlindsDown.Ui.Forms;

/// <summary>Where each popup goes. <see cref="Locations"/> is parallel to the input (newest first); null = not shown.</summary>
internal sealed record AlertStackPlacement(IReadOnlyList<Point?> Locations, int HiddenCount, Point? PillLocation);

/// <summary>
/// Stacks popups in the bottom-right corner of the work area: the newest sits in the corner, older ones
/// go upward. At most <see cref="MaxVisible"/> are shown (fewer if the screen is too short); the rest
/// collapse into a "+N more" pill above the stack. Pure arithmetic in device pixels, so it is tested
/// without creating windows.
/// </summary>
internal static class AlertStackLayout
{
    public const int MaxVisible = 4;

    public static AlertStackPlacement Compute(
        Rectangle workArea,
        IReadOnlyList<Size> newestFirst,
        Size pillSize,
        int margin,
        int spacing,
        int maxVisible = MaxVisible)
    {
        int count = newestFirst.Count;
        var locations = new Point?[count];
        if (count == 0)
        {
            return new AlertStackPlacement(locations, 0, null);
        }

        int top = workArea.Top + margin;
        int visible = 0;
        int nextBottom = workArea.Bottom - margin;
        var bottoms = new int[count + 1];
        bottoms[0] = nextBottom;

        // The newest popup is always shown, even if it is taller than the screen (then it is pinned to the top).
        while (visible < count && visible < maxVisible)
        {
            Size size = newestFirst[visible];
            int y = nextBottom - size.Height;
            if (y < top && visible > 0)
            {
                break;
            }

            locations[visible] = new Point(workArea.Right - margin - size.Width, Math.Max(y, workArea.Top));
            nextBottom = y - spacing;
            visible++;
            bottoms[visible] = nextBottom;
        }

        // Make room for the pill by hiding older popups if needed (never the newest).
        while (visible < count && visible > 1 && bottoms[visible] - pillSize.Height < top)
        {
            visible--;
            locations[visible] = null;
        }

        int hidden = count - visible;
        Point? pill = null;
        if (hidden > 0)
        {
            pill = new Point(workArea.Right - margin - pillSize.Width, Math.Max(bottoms[visible] - pillSize.Height, workArea.Top));
        }

        return new AlertStackPlacement(locations, hidden, pill);
    }
}
