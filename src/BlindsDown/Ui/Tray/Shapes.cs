using System.Drawing.Drawing2D;

namespace BlindsDown.Ui.Tray;

/// <summary>Vector shapes shared by the tray icon, the lock overlay and the OSD flash, so they look like one family.</summary>
internal static class Shapes
{
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius) =>
        RoundedRect(bounds, radius, radius, radius, radius);

    /// <summary>A rectangle with an individual radius per corner (0 = square corner).</summary>
    public static GraphicsPath RoundedRect(RectangleF bounds, float topLeft, float topRight, float bottomRight, float bottomLeft)
    {
        float max = Math.Min(bounds.Width, bounds.Height) / 2f;
        topLeft = Math.Clamp(topLeft, 0, max);
        topRight = Math.Clamp(topRight, 0, max);
        bottomRight = Math.Clamp(bottomRight, 0, max);
        bottomLeft = Math.Clamp(bottomLeft, 0, max);

        var path = new GraphicsPath();
        AddCorner(path, bounds.Left, bounds.Top, topLeft, 180);
        AddCorner(path, bounds.Right - (topRight * 2), bounds.Top, topRight, 270);
        AddCorner(path, bounds.Right - (bottomRight * 2), bounds.Bottom - (bottomRight * 2), bottomRight, 0);
        AddCorner(path, bounds.Left, bounds.Bottom - (bottomLeft * 2), bottomLeft, 90);
        path.CloseFigure();
        return path;

        static void AddCorner(GraphicsPath path, float x, float y, float radius, float startAngle)
        {
            if (radius <= 0)
            {
                // With a zero radius (x, y) is exactly the corner; a zero-length line keeps it square.
                path.AddLine(x, y, x, y);
                return;
            }

            path.AddArc(x, y, radius * 2, radius * 2, startAngle, 90);
        }
    }

    /// <summary>
    /// A padlock filling <paramref name="bounds"/> (best with a roughly square box). <paramref name="open"/>
    /// lifts the shackle and frees its left leg. The keyhole is painted in <paramref name="holeColor"/>,
    /// which should be the colour behind the glyph.
    /// </summary>
    public static void DrawPadlock(Graphics g, RectangleF bounds, Color color, Color holeColor, bool open)
    {
        float w = bounds.Width;
        float h = bounds.Height;
        float cx = bounds.Left + (w / 2f);
        float stroke = Math.Max(1f, w * 0.13f);
        float lift = h * 0.14f;

        var body = new RectangleF(bounds.Left + (w * 0.1f), bounds.Top + (h * 0.44f), w * 0.8f, h * 0.56f);
        float shackleRadius = ((w * 0.58f) - stroke) / 2f;
        float arcCenterY = bounds.Top + (stroke / 2f) + shackleRadius + (open ? 0 : lift);
        float legBottom = body.Top + stroke;

        using (var pen = new Pen(color, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
        using (var shackle = new GraphicsPath())
        {
            float leftLegBottom = open ? body.Top - lift : legBottom;
            shackle.AddLine(cx - shackleRadius, leftLegBottom, cx - shackleRadius, arcCenterY);
            shackle.AddArc(cx - shackleRadius, arcCenterY - shackleRadius, shackleRadius * 2, shackleRadius * 2, 180, 180);
            shackle.AddLine(cx + shackleRadius, arcCenterY, cx + shackleRadius, legBottom);
            g.DrawPath(pen, shackle);
        }

        using (var brush = new SolidBrush(color))
        using (GraphicsPath bodyPath = RoundedRect(body, w * 0.14f))
        {
            g.FillPath(brush, bodyPath);
        }

        float holeRadius = w * 0.085f;
        float holeCy = body.Top + (body.Height * 0.42f);
        using (var hole = new SolidBrush(holeColor))
        {
            g.FillEllipse(hole, cx - holeRadius, holeCy - holeRadius, holeRadius * 2, holeRadius * 2);
            float stemWidth = holeRadius * 0.9f;
            g.FillRectangle(hole, cx - (stemWidth / 2f), holeCy, stemWidth, body.Height * 0.3f);
        }
    }
}
