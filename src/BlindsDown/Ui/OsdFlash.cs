using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Ui;

/// <summary>
/// A big, short-lived on-screen confirmation in the middle of the active monitor (like the
/// Windows volume flyout) whenever the lock changes: "Microphones locked" / "Unlocked" /
/// "Partially locked". Fades out after ~1.5 s, click-through, never takes focus. UI thread only.
///
/// "Active monitor" is the one showing the foreground window, i.e. where the user is looking (the
/// hotkey may have been pressed in a fullscreen game on a secondary screen). A new Show() replaces the
/// current flash in place and restarts its timer.
/// </summary>
internal sealed class OsdFlash : IDisposable
{
    private const int HoldMs = 1100;
    private const int FadeMs = 400;
    private const int FrameMs = 30;

    private readonly System.Windows.Forms.Timer _timer = new();
    private OsdWindow? _window;
    private long _fadeStartMs;
    private bool _disposed;

    public OsdFlash()
    {
        _timer.Tick += OnTick;
    }

    /// <summary>Keeps the flash out of screenshots and screen shares (mirror AppSettings.HideOverlayFromScreenCapture).</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Where to show the flash; tests point this at an off-screen area so nothing becomes visible.</summary>
    internal Func<MonitorArea> MonitorSource { get; set; } = MonitorArea.Active;

    /// <summary>The flash window, once one has been shown.</summary>
    internal Form? Window => _window;

    /// <summary>Flashes the new state. <paramref name="cameras"/>: the change covered the cameras too (the camera
    /// helper locked or unlocked them), so the title says "Microphones and cameras locked".</summary>
    public void Show(LockState state, string? detail = null, bool cameras = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            MonitorArea monitor = MonitorSource();
            Size size = OverlayPainter.MeasureOsd(state, detail, monitor.Scale, cameras);
            Rectangle bounds = OverlayLayout.CenterIn(monitor.WorkingArea, size);
            _window ??= new OsdWindow();
            _window.ShowContent(state, detail, monitor.Scale, bounds, HideFromScreenCapture, cameras);

            _timer.Stop();
            _timer.Interval = HoldMs;
            _fadeStartMs = Environment.TickCount64 + HoldMs;
            _timer.Start();
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            // The flash is a courtesy; the tray icon, pill and sound still confirm the change.
            Log.Error("Could not show the on-screen confirmation", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
        _window?.Dispose();
        _window = null;
    }

    /// <summary>Fade-out progress 0..1 to opacity, easing in so the flash lingers, then slips away.</summary>
    internal static double FadeOpacity(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return OsdWindow.MaxOpacity * (1 - (t * t));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            _timer.Stop();
            return;
        }

        _timer.Interval = FrameMs;
        double progress = (Environment.TickCount64 - _fadeStartMs) / (double)FadeMs;
        if (progress >= 1)
        {
            _timer.Stop();
            _window.Hide();
            return;
        }

        _window.Opacity = FadeOpacity(progress); // always below 1.0, see OsdWindow
    }
}
