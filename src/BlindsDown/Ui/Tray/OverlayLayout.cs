using BlindsDown.Core.Settings;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Tray;

/// <summary>One monitor as the overlay sees it: physical-pixel rectangles (the app is per-monitor DPI aware) and its scale.</summary>
internal sealed record MonitorArea(Rectangle Bounds, Rectangle WorkingArea, float Scale)
{
    /// <summary>All monitors with their own effective DPI (mixed-DPI setups get a correctly sized indicator on each).</summary>
    public static IReadOnlyList<MonitorArea> All() =>
        [.. Screen.AllScreens.Select(s => new MonitorArea(s.Bounds, s.WorkingArea, ScaleOf(s.Bounds)))];

    /// <summary>The monitor showing the foreground window, or the primary monitor if there is none.</summary>
    public static MonitorArea Active()
    {
        nint foreground = TrayNative.GetForegroundWindow();
        Screen screen = (foreground != 0 ? Screen.FromHandle(foreground) : Screen.PrimaryScreen) ?? Screen.AllScreens[0];
        return new MonitorArea(screen.Bounds, screen.WorkingArea, ScaleOf(screen.Bounds));
    }

    private static float ScaleOf(Rectangle bounds)
    {
        var center = new TrayNative.Point { X = bounds.Left + (bounds.Width / 2), Y = bounds.Top + (bounds.Height / 2) };
        nint monitor = TrayNative.MonitorFromPoint(center, TrayNative.MonitorDefaultToNearest);
        return TrayNative.GetMonitorDpi(monitor) / 96f;
    }
}

/// <summary>Pure placement maths for the lock overlay and the OSD flash (unit-tested).</summary>
internal static class OverlayLayout
{
    /// <summary>Logical pixels between the pill and the edges of the working area.</summary>
    public const int PillMargin = 12;

    /// <summary>Thickness of the optional screen-edge lines at 100% scaling.</summary>
    public const int EdgeThickness = 4;

    public static int Scale(float logical, float scale) =>
        Math.Max(1, (int)Math.Round(logical * scale, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Places a box of <paramref name="size"/> in a corner of the working area (so it never covers the
    /// taskbar), <paramref name="margin"/> pixels in from both edges, and keeps it inside even on tiny screens.
    /// </summary>
    public static Rectangle PlaceInCorner(Rectangle workingArea, Size size, OverlayCorner corner, int margin)
    {
        bool left = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft;
        bool top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;
        int x = left ? workingArea.Left + margin : workingArea.Right - margin - size.Width;
        int y = top ? workingArea.Top + margin : workingArea.Bottom - margin - size.Height;
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - size.Width));
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - size.Height));
        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>Four non-overlapping strips along the edges of <paramref name="bounds"/>: top, bottom, left, right.</summary>
    public static Rectangle[] EdgeStrips(Rectangle bounds, int thickness)
    {
        int t = Math.Clamp(thickness, 1, Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 4));
        int sideHeight = Math.Max(0, bounds.Height - (2 * t));
        return
        [
            new Rectangle(bounds.Left, bounds.Top, bounds.Width, t),
            new Rectangle(bounds.Left, bounds.Bottom - t, bounds.Width, t),
            new Rectangle(bounds.Left, bounds.Top + t, t, sideHeight),
            new Rectangle(bounds.Right - t, bounds.Top + t, t, sideHeight),
        ];
    }

    /// <summary>Centres <paramref name="size"/> in <paramref name="area"/>, never extending past its top-left corner.</summary>
    public static Rectangle CenterIn(Rectangle area, Size size) =>
        new(
            area.Left + Math.Max(0, (area.Width - size.Width) / 2),
            area.Top + Math.Max(0, (area.Height - size.Height) / 2),
            size.Width,
            size.Height);
}
