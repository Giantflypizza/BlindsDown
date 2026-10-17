using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Ui;

/// <summary>
/// Draws the tray icon at an exact pixel size: unlocked = window with blinds UP (outline,
/// neutral colour); locked = blinds DOWN (filled, red); partially locked = blinds down, amber with a
/// warning mark; locking/unlocking = a transitional look. Light glyphs on a dark taskbar.
///
/// The locked icon differs in SHAPE (a solid block instead of an outline), so it is recognisable for
/// colour-blind users and on a red accent-coloured taskbar. All horizontal edges are snapped to whole
/// pixels so the slats stay crisp at 16 px; only the rounded corners are antialiased.
/// </summary>
internal static class TrayIconRenderer
{
    internal const int MinSize = 16;
    internal const int MaxSize = 256;

    /// <summary>Slat gaps of the closed blinds (white reads as "blinds" on every taskbar colour, even red).</summary>
    private static readonly Color GapColor = Color.FromArgb(255, 255, 255);

    /// <summary>Returns a new Icon the caller owns (dispose it). It owns its HICON, so nothing leaks.</summary>
    public static Icon Render(LockState state, int sizePx, bool darkTaskbar)
    {
        using Bitmap bitmap = RenderBitmap(state, sizePx, darkTaskbar);
        return IconBuilder.FromBitmap(bitmap);
    }

    /// <summary>The colour of glyphs that have no state colour: white on a dark taskbar, near-black on a light one.</summary>
    internal static Color NeutralColor(bool darkBackground) => darkBackground ? Color.White : Theme.LightText;

    /// <summary>Same drawing as <see cref="Render"/>, as a bitmap (used for menu images and visual QA).</summary>
    internal static Bitmap RenderBitmap(LockState state, int sizePx, bool darkTaskbar)
    {
        int size = Math.Clamp(sizePx, MinSize, MaxSize);
        BlindsLayout layout = BlindsLayout.For(size);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half; // pixel i covers [i, i+1): whole-number edges are crisp
                g.CompositingQuality = CompositingQuality.HighQuality;

                Color neutral = NeutralColor(darkTaskbar);
                switch (state)
                {
                    case LockState.Locked:
                        DrawClosed(g, layout, Theme.Locked);
                        break;
                    case LockState.PartiallyLocked:
                        DrawClosed(g, layout, Theme.Warning);
                        break;
                    case LockState.Locking or LockState.Unlocking:
                        DrawOpen(g, layout, neutral, 1);
                        break;
                    default:
                        DrawOpen(g, layout, neutral, 0);
                        break;
                }
            }

            if (state == LockState.PartiallyLocked)
            {
                DrawWarningBadge(bitmap, layout);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Blinds fully down: a solid block with light slat gaps.</summary>
    private static void DrawClosed(Graphics g, BlindsLayout layout, Color color)
    {
        using (var brush = new SolidBrush(color))
        using (GraphicsPath body = Shapes.RoundedRect(layout.Frame, layout.Radius))
        {
            g.FillPath(brush, body);
        }

        using var gap = new SolidBrush(GapColor);
        foreach (int row in layout.GapRows)
        {
            g.FillRectangle(gap, layout.InnerLeft, row, layout.InnerWidth, layout.Gap);
        }
    }

    /// <summary>
    /// The window outline with the blinds pulled up. <paramref name="slatsDown"/> = 0 shows only the
    /// stacked headrail (unlocked); more shows the blinds partway down (locking/unlocking).
    /// </summary>
    private static void DrawOpen(Graphics g, BlindsLayout layout, Color color, int slatsDown)
    {
        using var brush = new SolidBrush(color);
        using (GraphicsPath ring = Shapes.RoundedRect(layout.Frame, layout.Radius))
        {
            RectangleF inner = RectangleF.Inflate(layout.Frame, -layout.Stroke, -layout.Stroke);
            using GraphicsPath hole = Shapes.RoundedRect(inner, Math.Max(0.5f, layout.Radius - layout.Stroke));
            ring.AddPath(hole, connect: false);
            ring.FillMode = FillMode.Alternate;
            g.FillPath(brush, ring);
        }

        // The stacked blinds: headrail plus one compressed slat (unlocked), or more slats (transition).
        int stackBottom = slatsDown == 0
            ? layout.GapRows[0] + layout.Gap + Math.Max(1, layout.Slat / 2)
            : layout.GapRows[slatsDown] + layout.Gap + layout.Slat;
        var stack = new RectangleF(layout.Frame.Left, layout.Frame.Top, layout.Frame.Width, stackBottom - layout.Frame.Top);
        using (GraphicsPath stackPath = Shapes.RoundedRect(stack, layout.Radius, layout.Radius, 0, 0))
        {
            g.FillPath(brush, stackPath);
        }

        // Gaps are punched out to transparent so the taskbar shows through, like a monochrome system icon.
        CompositingMode previous = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            for (int i = 0; i <= slatsDown; i++)
            {
                g.FillRectangle(clear, layout.InnerLeft, layout.GapRows[i], layout.InnerWidth, layout.Gap);
            }
        }

        g.CompositingMode = previous;

        if (slatsDown == 0 && layout.Size >= 20)
        {
            DrawPullCord(g, layout, brush, stackBottom);
        }
    }

    /// <summary>A short cord hanging from the raised blinds: makes "blinds up" read as blinds, not as a title bar.</summary>
    private static void DrawPullCord(Graphics g, BlindsLayout layout, Brush brush, int stackBottom)
    {
        int cordWidth = Math.Max(1, layout.Stroke / 2);
        float x = layout.InnerLeft + layout.InnerWidth - (layout.Slat * 1.5f) - cordWidth;
        float x0 = MathF.Round(x);
        float length = MathF.Round((layout.Frame.Bottom - stackBottom) * 0.38f);
        g.FillRectangle(brush, x0, stackBottom, cordWidth, length);
        float knob = Math.Max(3f, cordWidth * 3f);
        g.FillEllipse(brush, x0 + (cordWidth / 2f) - (knob / 2f), stackBottom + length - (knob / 3f), knob, knob);
    }

    /// <summary>
    /// A round amber "!" badge in the bottom-right corner. A transparent ring is first knocked out of the
    /// icon around the badge (antialiased, computed per pixel because GDI+ clipping is not antialiased)
    /// so the badge reads as separate from the amber body.
    /// </summary>
    private static void DrawWarningBadge(Bitmap bitmap, BlindsLayout layout)
    {
        int size = layout.Size;
        int diameter = (int)MathF.Round(size * 0.56f);
        int barWidth = Math.Max(1, (int)MathF.Round(diameter * 0.17f));
        if (((diameter + barWidth) & 1) == 1)
        {
            diameter++; // keeps the "!" bar on whole pixels
        }

        float center = size - (diameter / 2f);
        float knockout = (diameter / 2f) + Math.Max(1f, size / 16f);
        KnockOutDisk(bitmap, center, center, knockout);

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var badge = new SolidBrush(Theme.Warning))
        {
            g.FillEllipse(badge, size - diameter, size - diameter, diameter, diameter);
        }

        using var mark = new SolidBrush(Theme.LightText);
        float barX = center - (barWidth / 2f);
        float top = MathF.Round(size - diameter + (diameter * 0.2f));
        float dotSize = barWidth;
        float dotTop = MathF.Round(size - (diameter * 0.2f) - dotSize);
        float barBottom = dotTop - Math.Max(1f, MathF.Round(diameter * 0.1f));
        g.FillRectangle(mark, barX, top, barWidth, barBottom - top);
        g.FillRectangle(mark, barX, dotTop, barWidth, dotSize);
    }

    /// <summary>Multiplies alpha by (1 - coverage of a disk), with a one-pixel antialiasing ramp.</summary>
    private static void KnockOutDisk(Bitmap bitmap, float cx, float cy, float radius)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[bitmap.Width * 4];
            for (int y = 0; y < bitmap.Height; y++)
            {
                nint scan = data.Scan0 + (y * data.Stride);
                Marshal.Copy(scan, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float coverage = Math.Clamp(radius - MathF.Sqrt((dx * dx) + (dy * dy)) + 0.5f, 0f, 1f);
                    int alpha = (x * 4) + 3;
                    row[alpha] = (byte)MathF.Round(row[alpha] * (1f - coverage));
                }

                Marshal.Copy(row, 0, scan, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Whole-pixel geometry of the blinds glyph for one icon size. The pattern from top to bottom is:
    /// headrail, then four times (gap, slat) where the last slat is the bottom of the frame. Slat
    /// thickness is chosen so the pattern fits exactly; the headrail absorbs the remainder.
    /// </summary>
    internal sealed record BlindsLayout(
        int Size,
        Rectangle Frame,
        float Radius,
        int Stroke,
        int Slat,
        int Gap,
        IReadOnlyList<int> GapRows)
    {
        private const int GapCount = 4;

        public int InnerLeft => Frame.Left + Stroke;

        public int InnerWidth => Frame.Width - (2 * Stroke);

        public static BlindsLayout For(int size)
        {
            int inset = Math.Max(1, (int)Math.Round(size / 16.0, MidpointRounding.AwayFromZero));
            int stroke = Math.Max(1, (int)Math.Round(size / 14.0, MidpointRounding.AwayFromZero));
            int gap = Math.Max(1, (int)((size / 16.0 * 0.75) + 0.5));
            var frame = new Rectangle(inset, inset, size - (2 * inset), size - (2 * inset));
            int height = frame.Height;
            int minHeadrail = Math.Max(2, (int)Math.Round(height * 0.15, MidpointRounding.AwayFromZero));

            // height = headrail + GapCount * gap + (GapCount - 1) * slat + stroke (bottom rail)
            int slat = Math.Max(1, (height - minHeadrail - (GapCount * gap) - stroke) / (GapCount - 1));
            int headrail = height - (GapCount * gap) - ((GapCount - 1) * slat) - stroke;

            var rows = new List<int>(GapCount);
            int y = frame.Top + headrail;
            for (int i = 0; i < GapCount; i++)
            {
                rows.Add(y);
                y += gap + slat;
            }

            return new BlindsLayout(size, frame, frame.Width * 0.2f, stroke, slat, gap, rows);
        }
    }
}
