using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;
using BlindsDown.Ui.Tray;
using Microsoft.Win32;

namespace BlindsDown.Ui;

/// <summary>
/// The "impossible to miss" indicator while locked: a pill (e.g. "MIC LOCKED") in the chosen
/// corner of EVERY monitor, plus optional thin red lines along each screen edge. Click-through,
/// never takes focus, not in Alt+Tab or the taskbar, stays topmost, correct on mixed-DPI multi-monitor
/// setups, rebuilt on display changes. Optionally hidden from screen capture (WDA_EXCLUDEFROMCAPTURE).
/// Amber + "PARTIALLY LOCKED" when <see cref="LockState.PartiallyLocked"/>. Nothing shown when unlocked.
/// Use on the UI thread.
///
/// Staying on top: other topmost windows (games, video players, other overlays) can rise above ours,
/// so HWND_TOPMOST is re-asserted every 2 s and whenever the foreground window changes. The pill fades
/// to near-transparent while the mouse is over it, so it never hides what is underneath (for example
/// a maximised window's close button), while staying click-through as always.
/// </summary>
internal sealed class LockOverlayManager : IDisposable
{
    private const int TopmostIntervalMs = 2000;
    private const int RebuildDelayMs = 500;
    private const int HoverIntervalMs = 150;
    private const byte HoverOpacity = 56;

    /// <summary>
    /// Hook handle to owner. WINEVENT_OUTOFCONTEXT callbacks are delivered through the message loop of the
    /// thread that installed the hook (the UI thread), so this is only touched on that thread. The callback
    /// is a static function pointer, so there is no delegate that the GC could collect while hooked.
    /// </summary>
    private static readonly Dictionary<nint, LockOverlayManager> HookOwners = [];

    private readonly List<Piece> _pieces = [];
    private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = TopmostIntervalMs };
    private readonly System.Windows.Forms.Timer _rebuildTimer = new() { Interval = RebuildDelayMs };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = HoverIntervalMs };

    /// <summary>Marshals SystemEvents notifications (which may arrive on another thread) to the UI thread.</summary>
    private readonly Control _marshal = new();

    private LockState _state = LockState.Unlocked;
    private OverlayOptions _options = new(false, false, OverlayCorner.TopRight, true);
    private string? _builtSignature;
    private nint _foregroundHook;
    private bool _captureWarningLogged;
    private bool _disposed;

    /// <summary>Create on the UI thread.</summary>
    public LockOverlayManager()
    {
        _ = _marshal.Handle; // BeginInvoke needs a window handle on this (UI) thread
        _topmostTimer.Tick += OnTopmostTick;
        _rebuildTimer.Tick += OnRebuildTick;
        _hoverTimer.Tick += OnHoverTick;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Where the monitors are; tests point this at off-screen areas so nothing becomes visible.</summary>
    internal Func<IReadOnlyList<MonitorArea>> MonitorSource { get; set; } = MonitorArea.All;

    /// <summary>The windows currently showing the indicator (pills and edge lines).</summary>
    internal IReadOnlyList<OverlayWindow> Windows => [.. _pieces.Select(p => p.Window)];

    public void Update(LockState state, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
        {
            return;
        }

        _state = state;
        _options = new OverlayOptions(
            settings.ShowOverlay,
            settings.ShowScreenEdges,
            settings.OverlayCorner,
            settings.HideOverlayFromScreenCapture);
        Apply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Clear();
        _topmostTimer.Dispose();
        _rebuildTimer.Dispose();
        _hoverTimer.Dispose();
        _marshal.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (HookOwners.TryGetValue(hook, out LockOverlayManager? owner))
            {
                owner.ReassertTopmost();
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            Log.Error("Foreground hook callback failed", ex);
        }
    }

    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        bool locked = _state is LockState.Locked or LockState.PartiallyLocked;
        if (!locked || (!_options.ShowPill && !_options.ShowEdges))
        {
            Clear();
            return;
        }

        IReadOnlyList<MonitorArea> monitors = MonitorSource();
        string signature = Signature(monitors);
        if (signature == _builtSignature && _pieces.Count > 0)
        {
            ReassertTopmost();
            return;
        }

        // Build the new windows before removing the old ones, so the indicator never blinks off.
        List<Piece> previous = [.. _pieces];
        _pieces.Clear();
        foreach (MonitorArea monitor in monitors)
        {
            // One failing monitor must not take the indicator off the others.
            try
            {
                BuildMonitor(monitor);
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
            {
                Log.Error($"Could not show the lock indicator on the monitor at {monitor.Bounds}", ex);
            }
        }

        DisposePieces(previous);
        _builtSignature = signature;
        StartWatching();
    }

    private void BuildMonitor(MonitorArea monitor)
    {
        if (_options.ShowEdges)
        {
            int thickness = OverlayLayout.Scale(OverlayLayout.EdgeThickness, monitor.Scale);
            foreach (Rectangle strip in OverlayLayout.EdgeStrips(monitor.Bounds, thickness))
            {
                using Bitmap edge = OverlayPainter.RenderEdge(strip.Size, _state);
                AddPiece(strip, edge, Rectangle.Empty);
            }
        }

        if (_options.ShowPill)
        {
            using Bitmap pill = OverlayPainter.RenderPill(_state, monitor.Scale, out int padding);
            var body = new Size(pill.Width - (2 * padding), pill.Height - (2 * padding));
            int margin = OverlayLayout.Scale(OverlayLayout.PillMargin, monitor.Scale);
            Rectangle bodyBounds = OverlayLayout.PlaceInCorner(monitor.WorkingArea, body, _options.Corner, margin);
            AddPiece(Rectangle.Inflate(bodyBounds, padding, padding), pill, bodyBounds);
        }
    }

    /// <summary>Creates, fills and shows one window. Content is pushed before showing: an empty layered window is invisible.</summary>
    private void AddPiece(Rectangle bounds, Bitmap content, Rectangle hoverArea)
    {
        var window = new OverlayWindow(bounds, _options.HideFromCapture);
        try
        {
            _ = window.Handle;
            if (!window.Present(content, byte.MaxValue))
            {
                window.Dispose();
                return;
            }

            window.Show();
            window.ReassertTopmost();
            window.MonitorDpiChanged += ScheduleRebuild;
            if (_options.HideFromCapture && !window.IsExcludedFromCapture && !_captureWarningLogged)
            {
                _captureWarningLogged = true;
                Log.Warn("This Windows version can't hide the lock indicator from screen capture (needs Windows 10 2004+).");
            }

            _pieces.Add(new Piece(window, hoverArea));
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    private void Clear()
    {
        StopWatching();
        DisposePieces(_pieces);
        _pieces.Clear();
        _builtSignature = null;
    }

    private void DisposePieces(IEnumerable<Piece> pieces)
    {
        foreach (Piece piece in pieces)
        {
            piece.Window.MonitorDpiChanged -= ScheduleRebuild;
            piece.Window.Dispose();
        }
    }

    /// <summary>Everything the current windows depend on; when it is unchanged nothing is rebuilt.</summary>
    private string Signature(IReadOnlyList<MonitorArea> monitors) =>
        string.Join(
            ';',
            monitors.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Bounds}|{m.WorkingArea}|{m.Scale}"))
                .Prepend(string.Create(CultureInfo.InvariantCulture, $"{_state}|{_options}")));

    private unsafe void StartWatching()
    {
        if (_pieces.Count == 0)
        {
            return;
        }

        _topmostTimer.Start();
        if (_pieces.Exists(p => !p.HoverArea.IsEmpty))
        {
            _hoverTimer.Start();
        }

        if (_foregroundHook == 0)
        {
            _foregroundHook = TrayNative.SetWinEventHook(
                TrayNative.EventSystemForeground,
                TrayNative.EventSystemForeground,
                0,
                &OnWinEvent,
                0,
                0,
                TrayNative.WinEventOutOfContext | TrayNative.WinEventSkipOwnProcess);
            if (_foregroundHook == 0)
            {
                Log.Warn($"SetWinEventHook failed with error {TrayNative.GetLastError()}; relying on the 2 s timer.");
            }
            else
            {
                HookOwners[_foregroundHook] = this;
            }
        }
    }

    private void StopWatching()
    {
        _topmostTimer.Stop();
        _hoverTimer.Stop();
        _rebuildTimer.Stop();
        if (_foregroundHook != 0)
        {
            HookOwners.Remove(_foregroundHook);
            _ = TrayNative.UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    private void ReassertTopmost()
    {
        // Edges first, pills last, so a pill is never under an edge line.
        foreach (Piece piece in _pieces)
        {
            piece.Window.ReassertTopmost();
        }
    }

    private void OnTopmostTick(object? sender, EventArgs e) => ReassertTopmost();

    private void OnRebuildTick(object? sender, EventArgs e)
    {
        _rebuildTimer.Stop();
        Apply();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        Point cursor = Cursor.Position;
        foreach (Piece piece in _pieces)
        {
            bool over = !piece.HoverArea.IsEmpty && piece.HoverArea.Contains(cursor);
            if (over != piece.Faded)
            {
                piece.Faded = over;
                piece.Window.SetOpacity(over ? HoverOpacity : byte.MaxValue);
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostScheduleRebuild();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => PostScheduleRebuild();

    private void PostScheduleRebuild()
    {
        if (_disposed || !_marshal.IsHandleCreated)
        {
            return;
        }

        try
        {
            _marshal.BeginInvoke(ScheduleRebuild);
        }
        catch (InvalidOperationException)
        {
            // The marshalling control was destroyed during shutdown.
        }
    }

    /// <summary>Displays often change in bursts (several monitors, DPI plus resolution), so wait until they settle.</summary>
    private void ScheduleRebuild()
    {
        if (_disposed || _pieces.Count == 0)
        {
            return;
        }

        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    private sealed record OverlayOptions(bool ShowPill, bool ShowEdges, OverlayCorner Corner, bool HideFromCapture);

    private sealed class Piece(OverlayWindow window, Rectangle hoverArea)
    {
        public OverlayWindow Window { get; } = window;

        /// <summary>The pill body (fades while the mouse is over it); empty for edge lines.</summary>
        public Rectangle HoverArea { get; } = hoverArea;

        public bool Faded { get; set; }
    }
}
