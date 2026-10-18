using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// Draws the round status badge used by popups and the restore report: a filled circle in the severity
/// colour with "i", a check mark or "!". Drawn with vector strokes rather than a font glyph, so it is
/// crisp at every DPI and does not depend on which symbol fonts are installed.
/// </summary>
internal static class SeverityGlyph
{
    public static void Draw(Graphics g, RectangleF bounds, AlertSeverity severity)
    {
        float size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 2f)
        {
            return;
        }

        var box = new RectangleF(
            bounds.X + ((bounds.Width - size) / 2f),
            bounds.Y + ((bounds.Height - size) / 2f),
            size,
            size);

        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var fill = new SolidBrush(UiPalette.SeverityColor(severity)))
            {
                g.FillEllipse(fill, box);
            }

            Color ink = UiPalette.OnSeverityColor(severity);
            using var pen = new Pen(ink, size * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var dot = new SolidBrush(ink);
            float cx = box.X + (size / 2f);

            PointF At(float x, float y) => new(box.X + (x * size), box.Y + (y * size));

            switch (severity)
            {
                case AlertSeverity.Success:
                    g.DrawLines(pen, [At(0.29f, 0.52f), At(0.44f, 0.67f), At(0.72f, 0.37f)]);
                    break;
                case AlertSeverity.Info:
                    FillDot(g, dot, cx, box.Y + (size * 0.29f), size * 0.08f);
                    g.DrawLine(pen, At(0.5f, 0.46f), At(0.5f, 0.73f));
                    break;
                default:
                    g.DrawLine(pen, At(0.5f, 0.26f), At(0.5f, 0.55f));
                    FillDot(g, dot, cx, box.Y + (size * 0.73f), size * 0.08f);
                    break;
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void FillDot(Graphics g, Brush brush, float cx, float cy, float radius) =>
        g.FillEllipse(brush, cx - radius, cy - radius, radius * 2f, radius * 2f);
}

/// <summary>
/// A small control that shows a <see cref="SeverityGlyph"/> (WinForms' PictureBox is banned: it can load
/// URLs). Its size follows the font, which WinForms rescales on every DPI change, so the badge always
/// matches the text next to it.
/// </summary>
internal sealed class SeverityGlyphControl : Control
{
    private AlertSeverity _severity;

    public SeverityGlyphControl(AlertSeverity severity)
    {
        _severity = severity;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.Transparent;
        AutoSize = true;
        AccessibleRole = AccessibleRole.Graphic;
    }

    /// <summary>The badge shown; a status line (e.g. the camera helper's) changes it in place.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AlertSeverity Severity
    {
        get => _severity;
        set
        {
            if (_severity != value)
            {
                _severity = value;
                Invalidate();
            }
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int side = (int)Math.Round(FontHeight * 1.3);
        return new Size(side, side);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Parent?.PerformLayout(this, nameof(Font));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RectangleF r = ClientRectangle;
        r.Inflate(-1f, -1f);
        SeverityGlyph.Draw(e.Graphics, r, _severity);
    }
}
