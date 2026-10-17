namespace BlindsDown.Ui.Forms;

/// <summary>
/// The small "+N more" pill above the popup stack. It is only a counter: older popups come into view as
/// the visible ones are closed or time out.
/// </summary>
internal sealed class MorePillForm : NoActivateForm
{
    private const int PadX = 12;
    private const int PillHeight = 26;

    private const TextFormatFlags Flags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    private Font? _font;
    private int _fontDpi;
    private int _count;

    public MorePillForm()
    {
        AccessibleRole = AccessibleRole.StaticText;
        Text = "BlindsDown notifications";
    }

    protected override bool SmallCorners => true;

    public static string Label(int count) => "+" + count.ToString(System.Globalization.CultureInfo.CurrentCulture) + " more";

    /// <summary>Sets the count and resizes for the given DPI. Returns the new size.</summary>
    public Size SetCount(int count, int dpi)
    {
        _ = Handle;
        _count = count;
        AccessibleName = count == 1 ? "1 more notification" : Label(count)[1..] + " notifications";
        Font font = FontFor(dpi);
        Size text = TextRenderer.MeasureText(Label(count), font, Size.Empty, Flags);
        float scale = dpi / 96f;
        Size = new Size(text.Width + (int)Math.Round(PadX * 2 * scale), (int)Math.Round(PillHeight * scale));
        Invalidate();
        return Size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = SystemColors.Window;
        Color text = SystemColors.WindowText;
        e.Graphics.Clear(background);
        if (_font is not null)
        {
            TextRenderer.DrawText(e.Graphics, Label(_count), _font, ClientRectangle, UiPalette.Blend(background, text, 0.75f), Flags);
        }

        if (!HasSystemFrame)
        {
            using var pen = new Pen(UiPalette.Blend(background, text, 0.25f));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // The manager re-measures the pill on every layout pass.
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font?.Dispose();
            _font = null;
        }

        base.Dispose(disposing);
    }

    private Font FontFor(int dpi)
    {
        if (_font is null || _fontDpi != dpi)
        {
            _font?.Dispose();
            using Font system = Theme.BodyFont;
            _font = new Font(system.FontFamily, system.SizeInPoints * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = dpi;
        }

        return _font;
    }
}
