using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;

namespace BlindsDown.Ui.Tray;

/// <summary>
/// Draws the lock pill and the OSD flash with GDI+ at an explicit scale (monitor DPI / 96). Kept apart
/// from the windows so the exact same pixels can be rendered to PNG for visual checks.
/// Text uses greyscale antialiasing: ClearType needs an opaque, known background, and the pill is a
/// per-pixel-alpha bitmap.
/// </summary>
internal static class OverlayPainter
{
    private const float PillHeight = 28f;
    private const float PillPadLeft = 10f;
    private const float PillPadRight = 13f;
    private const float PillGlyph = 14f;
    private const float PillGlyphGap = 7f;
    private const float PillFontPx = 12.5f;
    private const float PillShadow = 4f;

    private const float OsdMinWidth = 280f;
    private const float OsdMaxWidth = 460f;
    private const float OsdMinHeight = 120f;
    private const float OsdPad = 24f;
    private const float OsdBadge = 60f;
    private const float OsdGap = 18f;
    private const float OsdTitlePx = 20f;
    private const float OsdDetailPx = 13.5f;
    private const float OsdLineGap = 4f;

    private static readonly Lazy<string> FontFamilyName = new(() =>
    {
        using Font body = Theme.BodyFont;
        return body.FontFamily.Name;
    });

    /// <summary>
    /// Text on the pill: "MIC LOCKED", or "MIC + CAMERA LOCKED" while the camera helper also has the cameras
    /// turned off. A partial lock says so whatever is locked, since that is what matters then.
    /// </summary>
    public static string PillText(LockState state, bool cameras = false) =>
        state == LockState.PartiallyLocked ? "PARTIALLY LOCKED"
        : cameras ? "MIC + CAMERA LOCKED"
        : "MIC LOCKED";

    public static Color StateColor(LockState state) => state switch
    {
        LockState.Locked => Theme.Locked,
        LockState.PartiallyLocked => Theme.Warning,
        LockState.Unlocked => Theme.Ok,

        // Locking/unlocking: a lighter cousin of the navy accent, which would vanish on a dark surface.
        _ => Color.FromArgb(78, 98, 140),
    };

    /// <summary>Text/glyph colour on top of <see cref="StateColor"/>: white, except dark on amber (white on amber is ~2:1 contrast).</summary>
    public static Color OnStateColor(LockState state) =>
        state == LockState.PartiallyLocked ? Theme.LightText : Color.White;

    /// <summary>
    /// The pill as a 32-bpp ARGB bitmap with a transparent background, including a soft shadow of
    /// <paramref name="padding"/> pixels on every side (the pill body is the bitmap minus the padding).
    /// </summary>
    public static Bitmap RenderPill(LockState state, float scale, out int padding, bool cameras = false)
    {
        padding = OverlayLayout.Scale(PillShadow, scale);
        Size body = MeasurePill(state, scale, cameras);
        var bitmap = new Bitmap(body.Width + (2 * padding), body.Height + (2 * padding), PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            DrawPill(g, new Rectangle(padding, padding, body.Width, body.Height), state, scale, cameras);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>A solid strip in the state colour for the optional screen-edge lines.</summary>
    public static Bitmap RenderEdge(Size size, LockState state)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(bitmap);
            g.Clear(StateColor(state));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Size of the pill body (without shadow) in physical pixels: the padding, padlock and gap at this scale plus
    /// the width of the text as it is really drawn (<see cref="DrawnTextWidth"/>), so the pill hugs any text at
    /// any DPI (the width is rounded up, never down).
    /// </summary>
    public static Size MeasurePill(LockState state, float scale, bool cameras = false)
    {
        using Font font = PillFont(scale);
        float textWidth = DrawnTextWidth(PillText(state, cameras), font);
        float width = ((PillPadLeft + PillGlyph + PillGlyphGap + PillPadRight) * scale) + textWidth;
        return new Size((int)MathF.Ceiling(width), OverlayLayout.Scale(PillHeight, scale));
    }

    public static void DrawPill(Graphics g, Rectangle body, LockState state, float scale, bool cameras = false)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Color fill = StateColor(state);
        Color ink = OnStateColor(state);
        float radius = body.Height / 2f;

        // Soft shadow: a few widening, fading outlines below the pill (cheap and needs no blur).
        int shadow = OverlayLayout.Scale(PillShadow, scale);
        for (int i = shadow; i >= 1; i--)
        {
            RectangleF ring = RectangleF.Inflate(body, i, i);
            ring.Offset(0, MathF.Max(1f, scale) * 0.75f);
            using var shade = new SolidBrush(Color.FromArgb((int)(28f * (shadow - i + 1) / shadow), 0, 0, 0));
            using GraphicsPath shadowPath = Shapes.RoundedRect(ring, radius + i);
            g.FillPath(shade, shadowPath);
        }

        using (GraphicsPath pill = Shapes.RoundedRect(body, radius))
        using (var brush = new SolidBrush(fill))
        using (var edge = new Pen(Darken(fill, 0.25f), MathF.Max(1f, scale)))
        {
            g.FillPath(brush, pill);
            g.DrawPath(edge, pill);
        }

        float glyph = PillGlyph * scale;
        var glyphBox = new RectangleF(body.Left + (PillPadLeft * scale), body.Top + ((body.Height - glyph) / 2f) - (scale * 0.5f), glyph, glyph);
        Shapes.DrawPadlock(g, glyphBox, ink, fill, open: false);

        using Font font = PillFont(scale);
        float textLeft = glyphBox.Right + (PillGlyphGap * scale);
        DrawCapsCentered(g, PillText(state, cameras), font, ink, textLeft, body.Top + (body.Height / 2f));
    }

    /// <summary>Size of the OSD flash in physical pixels (wider for longer text, within limits).</summary>
    public static Size MeasureOsd(LockState state, string? detail, float scale, bool cameras = false)
    {
        using Font title = new(FontFamilyName.Value, OsdTitlePx * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font small = new(FontFamilyName.Value, OsdDetailPx * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        float fixedWidth = ((2 * OsdPad) + OsdBadge + OsdGap) * scale;
        float maxText = (OsdMaxWidth * scale) - fixedWidth;
        SizeF titleSize = MeasureText(OsdTitle(state, cameras), title, maxText);
        SizeF detailSize = MeasureText(OsdDetail(state, detail, cameras), small, maxText);
        float width = Math.Clamp(fixedWidth + MathF.Max(titleSize.Width, detailSize.Width) + scale, OsdMinWidth * scale, OsdMaxWidth * scale);
        float height = MathF.Max(OsdMinHeight * scale, (2 * OsdPad * scale) + titleSize.Height + (OsdLineGap * scale) + detailSize.Height);
        return new Size((int)MathF.Ceiling(width), (int)MathF.Ceiling(height));
    }

    /// <summary>Paints the OSD flash: a coloured round badge with a padlock, a title and a detail line.</summary>
    public static void DrawOsd(Graphics g, Rectangle bounds, LockState state, string? detail, float scale, bool dark, bool cameras = false)
    {
        ArgumentNullException.ThrowIfNull(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Color surface = dark ? Theme.DarkSurface : Theme.LightSurface;
        Color text = dark ? Theme.DarkText : Theme.LightText;
        Color secondary = Blend(text, surface, 0.32f);
        using (var background = new SolidBrush(surface))
        {
            g.FillRectangle(background, bounds);
        }

        float badge = OsdBadge * scale;
        var badgeBox = new RectangleF(bounds.Left + (OsdPad * scale), bounds.Top + ((bounds.Height - badge) / 2f), badge, badge);
        Color accent = StateColor(state);
        using (var accentBrush = new SolidBrush(accent))
        {
            g.FillEllipse(accentBrush, badgeBox);
        }

        bool open = state is LockState.Unlocked or LockState.Unlocking;
        float glyph = badge * 0.5f;
        var glyphBox = new RectangleF(badgeBox.Left + ((badge - glyph) / 2f), badgeBox.Top + ((badge - glyph) / 2f) - (badge * 0.02f), glyph, glyph);
        Shapes.DrawPadlock(g, glyphBox, OnStateColor(state), accent, open);

        using Font title = new(FontFamilyName.Value, OsdTitlePx * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font small = new(FontFamilyName.Value, OsdDetailPx * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        float textLeft = badgeBox.Right + (OsdGap * scale);
        float textWidth = bounds.Right - (OsdPad * scale) - textLeft;
        string titleText = OsdTitle(state, cameras);
        string detailText = OsdDetail(state, detail, cameras);
        SizeF titleSize = MeasureText(titleText, title, textWidth);
        SizeF detailSize = MeasureText(detailText, small, textWidth);
        float blockHeight = titleSize.Height + (OsdLineGap * scale) + detailSize.Height;
        float top = bounds.Top + ((bounds.Height - blockHeight) / 2f);

        using StringFormat format = TypographicFormat(wrap: true);
        using (var titleBrush = new SolidBrush(text))
        {
            g.DrawString(titleText, title, titleBrush, new RectangleF(textLeft, top, textWidth, titleSize.Height + scale), format);
        }

        using var detailBrush = new SolidBrush(secondary);
        g.DrawString(detailText, small, detailBrush, new RectangleF(textLeft, top + titleSize.Height + (OsdLineGap * scale), textWidth, detailSize.Height + scale), format);
    }

    /// <summary>The flash's title. <paramref name="cameras"/>: the change covered the cameras too (camera helper).</summary>
    public static string OsdTitle(LockState state, bool cameras = false) => state switch
    {
        LockState.Locked => cameras ? "Microphones and cameras locked" : "Microphones locked",
        LockState.PartiallyLocked => "Partially locked",
        LockState.Locking => "Locking…",
        LockState.Unlocking => "Unlocking…",
        _ => cameras ? "Microphones and cameras unlocked" : "Microphones unlocked",
    };

    public static string OsdDetail(LockState state, string? detail, bool cameras = false) =>
        !string.IsNullOrWhiteSpace(detail)
            ? detail
            : state switch
            {
                LockState.Locked => cameras ? "Every microphone is muted and every camera is off" : "Every microphone is muted",
                LockState.PartiallyLocked => cameras ? "Some devices could not be locked" : "Some microphones could not be muted",
                LockState.Locking => cameras ? "Muting microphones and turning cameras off" : "Muting microphones",
                LockState.Unlocking => cameras ? "Restoring microphones and cameras" : "Restoring microphones",
                _ => cameras ? "Microphones and cameras are back as they were" : "Microphones are back as they were",
            };

    private static Font PillFont(float scale) =>
        new(FontFamilyName.Value, PillFontPx * scale, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>Draws single-line text so its capital letters are vertically centred on <paramref name="centerY"/>.</summary>
    private static void DrawCapsCentered(Graphics g, string text, Font font, Color color, float left, float centerY)
    {
        FontFamily family = font.FontFamily;
        float em = family.GetEmHeight(font.Style);
        float ascent = font.Size * family.GetCellAscent(font.Style) / em;
        float capHeight = font.Size * 0.7f; // Segoe UI cap height is ~0.7 em
        float baseline = centerY + (capHeight / 2f);
        using var brush = new SolidBrush(color);
        using StringFormat format = TypographicFormat(wrap: false);
        g.DrawString(text, font, brush, left, baseline - ascent, format);
    }

    /// <summary>
    /// How wide single-line text really comes out when drawn like the pill draws it. At small sizes GDI+ draws
    /// grid-fitted text narrower than MeasureString reports (about 0.7 px per letter at 100 %, so "MIC + CAMERA
    /// LOCKED" would get 13 px of extra space on its right), so the text is drawn once off-screen and its ink
    /// measured. Falls back to the measured width for text without ink.
    /// </summary>
    private static float DrawnTextWidth(string text, Font font)
    {
        SizeF measured = MeasureText(text, font);
        int width = (int)MathF.Ceiling(measured.Width) + 4;
        int height = (int)MathF.Ceiling(measured.Height) + 2;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.White);
            using StringFormat format = TypographicFormat(wrap: false);
            g.DrawString(text, font, brush, 0, 0, format);
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] pixels = new int[data.Stride / 4 * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int stride = data.Stride / 4;
            for (int x = width - 1; x >= 0; x--)
            {
                for (int y = 0; y < height; y++)
                {
                    if ((pixels[(y * stride) + x] >>> 24) != 0)
                    {
                        return x + 1;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return measured.Width;
    }

    private static SizeF MeasureText(string text, Font font, float maxWidth = float.MaxValue)
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        bool wrap = maxWidth < float.MaxValue;
        using StringFormat format = TypographicFormat(wrap);
        return wrap
            ? g.MeasureString(text, font, new SizeF(maxWidth, float.MaxValue), format)
            : g.MeasureString(text, font, PointF.Empty, format);
    }

    /// <summary>
    /// A private copy of GDI+'s typographic format (tight metrics, no padding). The GenericTypographic
    /// getter wraps a shared GDI+ object, so it is cloned before anything is changed.
    /// </summary>
    private static StringFormat TypographicFormat(bool wrap)
    {
        using StringFormat generic = StringFormat.GenericTypographic;
        var format = (StringFormat)generic.Clone();
        if (wrap)
        {
            format.Trimming = StringTrimming.EllipsisWord;
        }

        return format;
    }

    private static Color Darken(Color color, float amount) =>
        Color.FromArgb(color.A, (int)(color.R * (1 - amount)), (int)(color.G * (1 - amount)), (int)(color.B * (1 - amount)));

    /// <summary>Mixes <paramref name="amount"/> of <paramref name="other"/> into <paramref name="color"/>.</summary>
    private static Color Blend(Color color, Color other, float amount) =>
        Color.FromArgb(
            (int)((color.R * (1 - amount)) + (other.R * amount)),
            (int)((color.G * (1 - amount)) + (other.G * amount)),
            (int)((color.B * (1 - amount)) + (other.B * amount)));
}
