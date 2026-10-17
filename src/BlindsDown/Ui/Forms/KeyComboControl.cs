using System.Drawing.Drawing2D;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// Shows a hotkey as keyboard keys ("Ctrl" "Alt" "Shift" "M"), which is easier to read at a glance than
/// "Ctrl+Alt+Shift+M". All spacing is derived from the font height, so it scales with DPI exactly like text.
/// </summary>
internal sealed class KeyComboControl : Control
{
    private const TextFormatFlags Flags =
        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private readonly string[] _keys;

    public KeyComboControl(string hotkeyText)
    {
        _keys = hotkeyText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AutoSize = true;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = hotkeyText;
        Text = hotkeyText;
        DerivedFont.Follow(this, FontStyle.Bold, 1.1f);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Metrics m = Measure();
        int width = 0;
        for (int i = 0; i < _keys.Length; i++)
        {
            width += m.KeyWidths[i];
            if (i < _keys.Length - 1)
            {
                width += m.PlusWidth;
            }
        }

        return new Size(width + 2, m.KeyHeight + m.Shadow + 2);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);

        // The preferred size follows the font (DPI changes rescale fonts); let the parent table re-measure.
        Parent?.PerformLayout(this, nameof(Font));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Metrics m = Measure();
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color surface = Parent?.BackColor ?? SystemColors.Window;
        Color text = SystemColors.WindowText;
        Color keyFace = UiPalette.Blend(surface, text, UiPalette.IsDark ? 0.14f : 0.04f);
        Color keyEdge = UiPalette.Blend(surface, text, 0.35f);
        Color keyShadow = UiPalette.Blend(surface, text, 0.22f);
        var radius = new Size(m.Radius, m.Radius);

        using var face = new SolidBrush(keyFace);
        using var shadow = new SolidBrush(keyShadow);
        using var edge = new Pen(keyEdge, Math.Max(1f, m.KeyHeight / 28f));
        float x = 1f;
        for (int i = 0; i < _keys.Length; i++)
        {
            var key = new RectangleF(x, 1f, m.KeyWidths[i], m.KeyHeight);
            g.FillRoundedRectangle(shadow, key with { Y = key.Y + m.Shadow }, radius);
            g.FillRoundedRectangle(face, key, radius);
            g.DrawRoundedRectangle(edge, key, radius);
            TextRenderer.DrawText(g, _keys[i], Font, Rectangle.Round(key), text, Flags);
            x += m.KeyWidths[i];
            if (i < _keys.Length - 1)
            {
                TextRenderer.DrawText(g, "+", Font, Rectangle.Round(new RectangleF(x, 1f, m.PlusWidth, m.KeyHeight)), UiPalette.Blend(surface, text, 0.6f), Flags);
                x += m.PlusWidth;
            }
        }
    }

    private Metrics Measure()
    {
        int lineHeight = TextRenderer.MeasureText("Ag", Font, Size.Empty, Flags).Height;
        int padX = (int)Math.Round(lineHeight * 0.55);
        var widths = new int[_keys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            int textWidth = TextRenderer.MeasureText(_keys[i], Font, Size.Empty, Flags).Width;
            widths[i] = Math.Max(textWidth + (padX * 2), (int)Math.Round(lineHeight * 1.7));
        }

        return new Metrics(
            widths,
            KeyHeight: (int)Math.Round(lineHeight * 1.7),
            PlusWidth: (int)Math.Round(lineHeight * 1.1),
            Radius: Math.Max(2, (int)Math.Round(lineHeight * 0.25)),
            Shadow: Math.Max(1, (int)Math.Round(lineHeight * 0.1)));
    }

    private readonly record struct Metrics(int[] KeyWidths, int KeyHeight, int PlusWidth, int Radius, int Shadow);
}
