using System.Drawing.Drawing2D;

namespace BlindsDown.Ui.Forms;

internal enum PopupPart
{
    None,
    Body,
    Close,
    Action,
}

/// <summary>What is under the mouse in a popup. <see cref="Index"/> is the action button index.</summary>
internal readonly record struct PopupHit(PopupPart Part, int Index = -1);

/// <summary>
/// Measures and paints one popup at a given DPI. All sizes are logical (96 DPI) constants scaled by
/// dpi / 96, and the fonts are created in pixels for that DPI, so the result is identical whether it
/// is painted into the window or into a bitmap for visual checks.
/// </summary>
internal sealed class AlertPopupLayout : IDisposable
{
    private const int TotalWidth = 400;
    private const int StripWidth = 4;
    private const int PadLeft = 14;
    private const int PadTop = 12;
    private const int PadRight = 16;
    private const int PadBottom = 14;
    private const int IconSize = 20;
    private const int IconGap = 10;
    private const int CloseSize = 28;
    private const int CloseInset = 6;
    private const int TitleGap = 3;
    private const int ButtonsGap = 12;
    private const int ButtonHeight = 28;
    private const int ButtonPadX = 14;
    private const int ButtonSpacing = 8;
    private const int MaxTitleLines = 2;
    private const int MaxMessageLines = 10;

    private const TextFormatFlags WrapFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;

    private const TextFormatFlags ButtonFlags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

    private readonly AlertModel _model;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly List<Rectangle> _buttons = [];
    private readonly float _scale;

    private AlertPopupLayout(AlertModel model, int dpi)
    {
        _model = model;
        _scale = dpi / 96f;
        using (Font system = Theme.BodyFont)
        {
            _bodyFont = new Font(system.FontFamily, system.SizeInPoints * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            _titleFont = new Font(system.FontFamily, (system.SizeInPoints + 1f) * dpi / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        int width = S(TotalWidth);
        int textLeft = S(StripWidth + PadLeft + IconSize + IconGap);
        CloseBounds = new Rectangle(width - S(CloseInset + CloseSize), S(CloseInset), S(CloseSize), S(CloseSize));
        int titleWidth = CloseBounds.Left - S(4) - textLeft;
        int messageWidth = width - S(PadRight) - textLeft;

        int titleLine = LineHeight(_titleFont);
        int titleHeight = Math.Min(Measure(model.Title, _titleFont, titleWidth).Height, titleLine * MaxTitleLines);
        TitleBounds = new Rectangle(textLeft, S(PadTop), titleWidth, Math.Max(titleHeight, titleLine));
        IconBounds = new Rectangle(S(StripWidth + PadLeft), TitleBounds.Top + ((titleLine - S(IconSize)) / 2), S(IconSize), S(IconSize));

        int y = TitleBounds.Bottom;
        if (!string.IsNullOrWhiteSpace(model.Message))
        {
            int messageHeight = Math.Min(Measure(model.Message, _bodyFont, messageWidth).Height, LineHeight(_bodyFont) * MaxMessageLines);
            MessageBounds = new Rectangle(textLeft, y + S(TitleGap), messageWidth, messageHeight);
            y = MessageBounds.Bottom;
        }

        IReadOnlyList<AlertAction> actions = model.Actions ?? [];
        if (actions.Count > 0)
        {
            int x = textLeft;
            int rowTop = y + S(ButtonsGap);
            int maxRight = width - S(PadRight);
            foreach (AlertAction action in actions)
            {
                int labelWidth = TextRenderer.MeasureText(action.Label, _bodyFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                int buttonWidth = Math.Min(labelWidth + S(ButtonPadX * 2), maxRight - textLeft);
                if (x > textLeft && x + buttonWidth > maxRight)
                {
                    x = textLeft;
                    rowTop += S(ButtonHeight + ButtonSpacing);
                }

                _buttons.Add(new Rectangle(x, rowTop, buttonWidth, S(ButtonHeight)));
                x += buttonWidth + S(ButtonSpacing);
            }

            y = rowTop + S(ButtonHeight);
        }

        int height = Math.Max(y + S(PadBottom), CloseBounds.Bottom + S(CloseInset));
        Size = new Size(width, height);
        StripBounds = new Rectangle(0, 0, S(StripWidth), height);
    }

    public Size Size { get; }

    public Rectangle StripBounds { get; }

    public Rectangle IconBounds { get; }

    public Rectangle TitleBounds { get; }

    public Rectangle MessageBounds { get; }

    public Rectangle CloseBounds { get; }

    public IReadOnlyList<Rectangle> ButtonBounds => _buttons;

    public static AlertPopupLayout Create(AlertModel model, int dpi) => new(model, dpi);

    public PopupHit HitTest(Point point)
    {
        if (CloseBounds.Contains(point))
        {
            return new PopupHit(PopupPart.Close);
        }

        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Contains(point))
            {
                return new PopupHit(PopupPart.Action, i);
            }
        }

        return new Rectangle(Point.Empty, Size).Contains(point) ? new PopupHit(PopupPart.Body) : default;
    }

    /// <summary>Paints the popup. <paramref name="drawOutline"/> is false on Windows 11, where DWM draws a rounded outline.</summary>
    public void Paint(Graphics g, PopupHit hover, PopupHit pressed, bool drawOutline)
    {
        Color background = SystemColors.Window;
        Color text = SystemColors.WindowText;
        g.Clear(background);

        using (var strip = new SolidBrush(UiPalette.SeverityColor(_model.Severity)))
        {
            g.FillRectangle(strip, StripBounds);
        }

        SeverityGlyph.Draw(g, IconBounds, _model.Severity);
        TextRenderer.DrawText(g, _model.Title, _titleFont, TitleBounds, text, WrapFlags | TextFormatFlags.EndEllipsis);
        if (!MessageBounds.IsEmpty)
        {
            // Ellipsis: a message longer than MaxMessageLines is cut, and the cut must be visible.
            TextRenderer.DrawText(g, _model.Message, _bodyFont, MessageBounds, UiPalette.Blend(background, text, 0.82f), WrapFlags | TextFormatFlags.EndEllipsis);
        }

        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            PaintClose(g, background, text, hover.Part == PopupPart.Close, pressed.Part == PopupPart.Close);
            IReadOnlyList<AlertAction> actions = _model.Actions ?? [];
            for (int i = 0; i < _buttons.Count; i++)
            {
                bool isHover = hover.Part == PopupPart.Action && hover.Index == i;
                bool isPressed = pressed.Part == PopupPart.Action && pressed.Index == i;
                PaintButton(g, _buttons[i], actions[i].Label, primary: i == 0, isHover, isPressed, background);
            }
        }
        finally
        {
            g.Restore(state);
        }

        if (drawOutline)
        {
            using var pen = new Pen(UiPalette.Blend(background, text, 0.25f));
            g.DrawRectangle(pen, 0, 0, Size.Width - 1, Size.Height - 1);
        }
    }

    public void Dispose()
    {
        _titleFont.Dispose();
        _bodyFont.Dispose();
    }

    private void PaintClose(Graphics g, Color background, Color text, bool hover, bool pressed)
    {
        if (hover || pressed)
        {
            using var fill = new SolidBrush(UiPalette.Blend(background, text, pressed ? 0.16f : 0.08f));
            g.FillRoundedRectangle(fill, CloseBounds, new Size(S(4), S(4)));
        }

        float half = 5f * _scale;
        float cx = CloseBounds.Left + (CloseBounds.Width / 2f);
        float cy = CloseBounds.Top + (CloseBounds.Height / 2f);
        using var pen = new Pen(UiPalette.Blend(background, text, hover ? 0.9f : 0.6f), Math.Max(1f, 1.25f * _scale));
        g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
    }

    private void PaintButton(Graphics g, Rectangle bounds, string label, bool primary, bool hover, bool pressed, Color background)
    {
        Color accent = UiPalette.Accent;
        var radius = new Size(S(4), S(4));
        RectangleF box = bounds;
        box.Inflate(-0.5f, -0.5f);
        Color labelColor;
        if (primary)
        {
            Color fill = pressed ? UiPalette.Blend(accent, background, 0.25f) : hover ? UiPalette.Blend(accent, background, 0.12f) : accent;
            using var brush = new SolidBrush(fill);
            g.FillRoundedRectangle(brush, box, radius);
            labelColor = UiPalette.IsDark ? Color.FromArgb(20, 22, 28) : Color.White;
        }
        else
        {
            if (hover || pressed)
            {
                using var brush = new SolidBrush(UiPalette.Blend(background, accent, pressed ? 0.2f : 0.1f));
                g.FillRoundedRectangle(brush, box, radius);
            }

            using var pen = new Pen(accent, Math.Max(1f, _scale));
            g.DrawRoundedRectangle(pen, box, radius);
            labelColor = accent;
        }

        TextRenderer.DrawText(g, label, _bodyFont, bounds, labelColor, ButtonFlags);
    }

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private static int LineHeight(Font font) =>
        TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;

    private static Size Measure(string text, Font font, int width) =>
        string.IsNullOrEmpty(text) ? Size.Empty : TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), WrapFlags);
}
