using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

/// <summary>
/// Renders the picture at the top of the README (docs/images/demo.png): what the user sees right after locking
/// with the camera helper, on a plain desktop. Every part is drawn by the app's own painting code, with the real
/// texts and the real placement rules: the "MIC + CAMERA LOCKED" badge in the top-right corner, the on-screen
/// confirmation, two popups for an app that tried the locked microphone and camera (the exact wording the app
/// uses for a microphone muted in hardware and a turned-off camera), and the tray icon. Only the desktop and
/// the shadows Windows would add are drawn here; the confirmation sits a little higher than on a full-size screen,
/// where it is centred, so it doesn't cover the popups in this small picture.
/// Opt-in like the other visual checks: set BLINDSDOWN_QA_DIR, then copy readme-demo.png to docs/images/demo.png.
/// </summary>
public class ReadmePictureTests
{
    /// <summary>Logical size of the desktop: about as wide as the README column, so text shows at its real size.</summary>
    private const int Width = 840;
    private const int Height = 520;
    private const int TaskbarHeight = 40;

    /// <summary>Rendered at 150 %, so the picture stays sharp on high-DPI screens.</summary>
    private const float Scale = 1.5f;

    [Fact]
    public void RenderReadmePicture()
    {
        string dir = VisualQaTests.OutputDirectory();
        string path = Path.Combine(dir, "readme-demo.png");
        using Bitmap picture = Render();
        picture.Save(path, ImageFormat.Png);
        Assert.True(new FileInfo(path).Length > 0);
    }

    private static Bitmap Render()
    {
        int dpi = (int)(96 * Scale);
        var monitor = new Rectangle(0, 0, Px(Width), Px(Height));
        var work = new Rectangle(0, 0, monitor.Width, monitor.Height - Px(TaskbarHeight));
        var desktop = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(desktop);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var wallpaper = new LinearGradientBrush(monitor, Color.FromArgb(34, 58, 96), Color.FromArgb(78, 104, 150), 30f))
        {
            g.FillRectangle(wallpaper, monitor);
        }

        using (var taskbar = new SolidBrush(Color.FromArgb(32, 32, 32)))
        {
            g.FillRectangle(taskbar, 0, work.Bottom, monitor.Width, monitor.Height - work.Bottom);
        }

        // The tray icon with the blinds down, near the right end of the taskbar.
        int iconSize = Px(16);
        using (Icon tray = TrayIconRenderer.Render(LockState.Locked, iconSize, darkTaskbar: true))
        using (Bitmap trayBitmap = tray.ToBitmap())
        {
            g.DrawImageUnscaled(trayBitmap, monitor.Right - Px(64), work.Bottom + ((monitor.Height - work.Bottom - iconSize) / 2));
        }

        // The badge, placed like the overlay places it.
        using (Bitmap pill = OverlayPainter.RenderPill(LockState.Locked, Scale, out int padding, cameras: true))
        {
            Rectangle body = OverlayLayout.PlaceInCorner(
                work,
                new Size(pill.Width - (2 * padding), pill.Height - (2 * padding)),
                OverlayCorner.TopRight,
                OverlayLayout.Scale(OverlayLayout.PillMargin, Scale));
            g.DrawImageUnscaled(pill, body.X - padding, body.Y - padding);
        }

        // The popups, stacked like AlertPopupManager stacks them (newest in the corner).
        AlertAction activity = new("Activity", () => { });
        AlertModel[] oldestFirst =
        [
            new(
                "Zoom Workplace opened your microphone",
                "Your microphones are locked, so Zoom Workplace only hears silence. Device: Microphone (USB Audio Device).",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [activity]),
            new(
                "Zoom Workplace tried your camera",
                "Your cameras are locked, so Zoom Workplace gets no picture. Device: HD Webcam C270.",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [activity]),
        ];
        List<Bitmap> newestFirst = [.. Enumerable.Reverse(oldestFirst).Select(m => AlertPopupForm.Render(m, dpi, drawOutline: false))];
        try
        {
            AlertStackPlacement placement = AlertStackLayout.Compute(work, newestFirst.ConvertAll(b => b.Size), Size.Empty, Px(12), Px(8));
            int stackTop = work.Bottom;
            for (int i = 0; i < newestFirst.Count; i++)
            {
                if (placement.Locations[i] is Point location)
                {
                    DrawWindow(g, newestFirst[i], location);
                    stackTop = Math.Min(stackTop, location.Y);
                }
            }

            // The on-screen confirmation, centred across the screen, in the room above the popups.
            Size osdSize = OverlayPainter.MeasureOsd(LockState.Locked, null, Scale, cameras: true);
            using var osd = new Bitmap(osdSize.Width, osdSize.Height, PixelFormat.Format24bppRgb);
            using (Graphics og = Graphics.FromImage(osd))
            {
                OverlayPainter.DrawOsd(og, new Rectangle(Point.Empty, osdSize), LockState.Locked, null, Scale, dark: false, cameras: true);
            }

            int osdX = (work.Width - osdSize.Width) / 2;
            int osdY = Math.Max(Px(56), (stackTop - Px(24) - osdSize.Height) / 2 + Px(24));
            DrawWindow(g, osd, new Point(osdX, osdY));
        }
        finally
        {
            newestFirst.ForEach(b => b.Dispose());
        }

        return desktop;
    }

    /// <summary>A window as Windows 11 shows it: rounded corners, a thin outline and a soft shadow.</summary>
    private static void DrawWindow(Graphics g, Bitmap content, Point location)
    {
        var bounds = new Rectangle(location, content.Size);
        int radius = Px(8);
        for (int i = Px(10); i >= 1; i--)
        {
            using GraphicsPath shadowPath = Rounded(Rectangle.Inflate(bounds with { Y = bounds.Y + Px(3) }, i, i), radius + i);
            using var shadow = new SolidBrush(Color.FromArgb(Math.Max(1, 9 - (i * 8 / Px(10))), 0, 0, 0));
            g.FillPath(shadow, shadowPath);
        }

        using GraphicsPath path = Rounded(bounds, radius);
        using (var texture = new TextureBrush(content, WrapMode.Clamp))
        {
            texture.TranslateTransform(bounds.X, bounds.Y);
            g.FillPath(texture, path);
        }

        using var outline = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
        g.DrawPath(outline, path);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int Px(float logical) => OverlayLayout.Scale(logical, Scale);
}
