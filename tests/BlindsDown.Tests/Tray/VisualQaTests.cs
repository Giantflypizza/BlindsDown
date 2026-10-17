using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BlindsDown.Core.Locking;
using BlindsDown.Ui;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

/// <summary>
/// Renders the tray icons into PNG contact sheets for a human (or agent) to look at. Opt-in: set
/// BLINDSDOWN_QA_DIR to an output folder (e.g. artifacts/qa). Pure GDI+ rendering; no windows are shown.
/// </summary>
public class VisualQaTests
{
    private static readonly int[] Sizes = [16, 20, 24, 28, 32, 40, 48];

    private static readonly LockState[] States =
        [LockState.Unlocked, LockState.Locking, LockState.Locked, LockState.PartiallyLocked, LockState.Unlocking];

    private static readonly (string Name, Color Color, bool Dark)[] Taskbars =
    [
        ("dark", Color.FromArgb(28, 28, 28), true),
        ("light", Color.FromArgb(238, 238, 238), false),
        ("accent-red", Color.FromArgb(150, 30, 30), true),
    ];

    [Fact]
    public void RenderTrayIconSheets()
    {
        string dir = OutputDirectory();

        // Zoomed sheet: nearest-neighbour so every pixel is visible.
        const int zoom = 6;
        int cell = (48 * zoom) + 16;
        using (var sheet = new Bitmap(cell * Sizes.Length, cell * States.Length * Taskbars.Length))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int rowIndex = 0;
            foreach ((string _, Color bg, bool dark) in Taskbars)
            {
                foreach (LockState state in States)
                {
                    using var bgBrush = new SolidBrush(bg);
                    g.FillRectangle(bgBrush, 0, rowIndex * cell, sheet.Width, cell);
                    for (int i = 0; i < Sizes.Length; i++)
                    {
                        using Bitmap icon = TrayIconRenderer.RenderBitmap(state, Sizes[i], dark);
                        g.DrawImage(icon, new Rectangle((i * cell) + 8, (rowIndex * cell) + 8, Sizes[i] * zoom, Sizes[i] * zoom));
                    }

                    rowIndex++;
                }
            }

            sheet.Save(Path.Combine(dir, "tray-icons-zoomed.png"), ImageFormat.Png);
        }

        // Actual size, through the real Icon round trip (what the shell receives), at 1x and 3x.
        foreach (int scale in new[] { 1, 3 })
        {
            int pad = 10 * scale;
            int width = pad + Sizes.Sum(s => (s * scale) + pad);
            int rowHeight = (48 * scale) + pad;
            using var sheet = new Bitmap(width, rowHeight * States.Length * Taskbars.Length);
            using Graphics g = Graphics.FromImage(sheet);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int rowIndex = 0;
            foreach ((string _, Color bg, bool dark) in Taskbars)
            {
                foreach (LockState state in States)
                {
                    using var bgBrush = new SolidBrush(bg);
                    g.FillRectangle(bgBrush, 0, rowIndex * rowHeight, sheet.Width, rowHeight);
                    int x = pad;
                    foreach (int size in Sizes)
                    {
                        using Icon icon = TrayIconRenderer.Render(state, size, dark);
                        using Bitmap bitmap = icon.ToBitmap();
                        g.DrawImage(bitmap, new Rectangle(x, (rowIndex * rowHeight) + (pad / 2), size * scale, size * scale));
                        x += (size * scale) + pad;
                    }

                    rowIndex++;
                }
            }

            sheet.Save(Path.Combine(dir, $"tray-icons-{scale}x.png"), ImageFormat.Png);
        }
    }

    [Fact]
    public void RenderOverlaySheets()
    {
        string dir = OutputDirectory();
        float[] scales = [1f, 1.25f, 1.5f, 2f];
        Color[] backgrounds = [Color.White, Color.FromArgb(32, 32, 36), Color.FromArgb(200, 40, 40), Color.FromArgb(240, 170, 30)];

        // Pills on several backgrounds, at every common scale, with their shadow.
        const int rowHeight = 90;
        using (var sheet = new Bitmap(1200, rowHeight * backgrounds.Length * 2))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            int row = 0;
            foreach (LockState state in new[] { LockState.Locked, LockState.PartiallyLocked })
            {
                foreach (Color bg in backgrounds)
                {
                    using var brush = new SolidBrush(bg);
                    g.FillRectangle(brush, 0, row * rowHeight, sheet.Width, rowHeight);
                    int x = 12;
                    foreach (float scale in scales)
                    {
                        using Bitmap pill = OverlayPainter.RenderPill(state, scale, out int _);
                        g.DrawImageUnscaled(pill, x, (row * rowHeight) + 8);
                        x += pill.Width + 16;
                    }

                    row++;
                }
            }

            sheet.Save(Path.Combine(dir, "overlay-pills.png"), ImageFormat.Png);
        }

        // OSD flashes, light and dark, every state, at 100% and 150%.
        foreach (float scale in new[] { 1f, 1.5f })
        {
            LockState[] states = [LockState.Locked, LockState.Unlocked, LockState.PartiallyLocked, LockState.Locking];
            int cellWidth = (int)(480 * scale);
            int cellHeight = (int)(150 * scale);
            using var sheet = new Bitmap(cellWidth * 2, cellHeight * states.Length);
            using Graphics g = Graphics.FromImage(sheet);
            g.Clear(Color.FromArgb(90, 110, 140));
            for (int i = 0; i < states.Length; i++)
            {
                for (int d = 0; d < 2; d++)
                {
                    bool dark = d == 1;
                    string? detail = states[i] == LockState.PartiallyLocked ? "Realtek Mikrofon could not be muted (in use by Zoom)" : null;
                    Size size = OverlayPainter.MeasureOsd(states[i], detail, scale);
                    using var osd = new Bitmap(size.Width, size.Height);
                    using (Graphics og = Graphics.FromImage(osd))
                    {
                        OverlayPainter.DrawOsd(og, new Rectangle(Point.Empty, size), states[i], detail, scale, dark);
                    }

                    g.DrawImageUnscaled(osd, (d * cellWidth) + 10, (i * cellHeight) + 10);
                }
            }

            sheet.Save(Path.Combine(dir, $"osd-{scale * 100:0}.png"), ImageFormat.Png);
        }
    }

    [Fact]
    public void RenderDesktopMock()
    {
        string dir = OutputDirectory();
        var monitor = new Rectangle(0, 0, 1280, 720);
        var workArea = new Rectangle(0, 0, 1280, 672); // 48 px taskbar
        using var desktop = new Bitmap(monitor.Width, monitor.Height);
        using Graphics g = Graphics.FromImage(desktop);
        using (var wallpaper = new LinearGradientBrush(monitor, Color.FromArgb(40, 70, 120), Color.FromArgb(150, 110, 160), 35f))
        {
            g.FillRectangle(wallpaper, monitor);
        }

        using (var window = new SolidBrush(Color.FromArgb(245, 245, 247)))
        using (var taskbar = new SolidBrush(Color.FromArgb(28, 28, 28)))
        {
            g.FillRectangle(window, 120, 0, 1160, 560); // a window maximised-ish under the pill
            g.FillRectangle(taskbar, 0, 672, 1280, 48);
        }

        using (Icon tray = TrayIconRenderer.Render(LockState.Locked, 16, darkTaskbar: true))
        using (Bitmap trayBitmap = tray.ToBitmap())
        {
            g.DrawImageUnscaled(trayBitmap, 1180, 688);
        }

        foreach (Rectangle strip in OverlayLayout.EdgeStrips(monitor, OverlayLayout.Scale(OverlayLayout.EdgeThickness, 1f)))
        {
            using Bitmap edge = OverlayPainter.RenderEdge(strip.Size, LockState.Locked);
            g.DrawImageUnscaled(edge, strip.Location);
        }

        using Bitmap pill = OverlayPainter.RenderPill(LockState.Locked, 1f, out int padding);
        Rectangle body = OverlayLayout.PlaceInCorner(workArea, new Size(pill.Width - (2 * padding), pill.Height - (2 * padding)), BlindsDown.Core.Settings.OverlayCorner.TopRight, OverlayLayout.PillMargin);
        g.DrawImageUnscaled(pill, body.X - padding, body.Y - padding);
        desktop.Save(Path.Combine(dir, "desktop-mock.png"), ImageFormat.Png);
    }

    internal static string OutputDirectory()
    {
        string? dir = Environment.GetEnvironmentVariable("BLINDSDOWN_QA_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "Set BLINDSDOWN_QA_DIR to write visual QA PNGs.");
        Directory.CreateDirectory(dir!);
        return dir!;
    }
}
