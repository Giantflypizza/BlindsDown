using System.Runtime.InteropServices;
using BlindsDown.Core.Time;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Ui;

internal enum AlertSeverity
{
    Info,
    Success,
    Warning,
    Danger,
}

internal sealed record AlertAction(string Label, Action OnClick);

/// <summary>
/// One popup. Sticky popups stay until dismissed (used for anything that happens while locked).
/// Alerts with the same <paramref name="GroupKey"/> update the popup that is still open instead of
/// stacking a new one (e.g. "something keeps unmuting this mic" every 30 s becomes one popup).
/// </summary>
internal sealed record AlertModel(
    string Title,
    string Message,
    AlertSeverity Severity,
    bool Sticky = false,
    IReadOnlyList<AlertAction>? Actions = null,
    string? GroupKey = null);

/// <summary>
/// BlindsDown's own notification popups (Windows 11 toasts are transient and get swallowed by
/// Do Not Disturb, so we don't rely on them). Bottom-right of the primary work area, stacked upward with
/// the newest closest to the corner, max 4 visible (older ones collapse into "+N more"), topmost, NEVER
/// steal focus (WS_EX_NOACTIVATE / ShowWithoutActivation), close button, optional action buttons,
/// auto-dismiss after ~8 s unless Sticky or the mouse is over it. Severity sets an accent colour strip.
/// Identical title+message within 3 s are coalesced into the popup that is still open (if the user already
/// closed it, the repeat is shown again rather than lost). Raises a UIA notification for screen readers.
/// UI thread only.
/// </summary>
internal sealed class AlertPopupManager : IDisposable
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(3);
    private const int Margin = 12;
    private const int Spacing = 8;

    /// <summary>Oldest first.</summary>
    private readonly List<AlertPopupForm> _popups = [];
    private readonly AlertCoalescer _coalescer = new(SystemClock.Instance, CoalesceWindow);
    private MorePillForm? _pill;
    private bool _closingAll;
    private bool _arranging;
    private bool _arrangeAgain;
    private bool _disposed;

    /// <summary>Keeps new popups out of screen sharing and recordings (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    /// <summary>Shows the alert. Returns true when a new popup appeared, false when it was merged into one
    /// that is already on screen (callers use this to avoid repeating sounds).</summary>
    public bool Show(AlertModel model)
    {
        if (_disposed)
        {
            return false;
        }

        if (model.GroupKey is not null)
        {
            string groupKey = AlertPopupForm.KeyFor(model);
            AlertPopupForm? open = _popups.FindLast(p => p.Key == groupKey && !p.IsDisposed);
            if (open is not null)
            {
                open.UpdateContent(model);
                return false;
            }
        }
        else if (_coalescer.IsRepeat(model.Title, model.Message))
        {
            string key = AlertCoalescer.KeyOf(model.Title, model.Message);
            AlertPopupForm? existing = _popups.FindLast(p => p.Key == key && !p.IsDisposed);
            if (existing is not null)
            {
                existing.Renew(model.Sticky);
                return false;
            }

            // The earlier popup is already gone (the user closed it, or DismissAll ran), so there is
            // nothing to merge into. Dropping the repeat would silently lose an alert, e.g. a second
            // "unmuted while locked" right after the user dismissed the first one.
        }

        var popup = new AlertPopupForm(model);
        popup.FormScreenCaptureMode = HideFromScreenCapture ? ScreenCaptureMode.HideWindow : ScreenCaptureMode.Allow;
        try
        {
            popup.FormClosed += OnPopupClosed;
            popup.SizeInvalidated += _ => Relayout();
            popup.WorkAreaChanged += Relayout;

            // Create the window inside the primary work area, so it gets that monitor's DPI from the start.
            Rectangle work = SystemInformation.WorkingArea;
            popup.Prepare(new Point(work.Right - 2, work.Bottom - 2));
            _popups.Add(popup);
            Relayout();
            popup.Announce();
            return true;
        }
        catch (ExternalException ex)
        {
            // Out of window handles, or the desktop is not available (session switch).
            Log.Error($"Could not show the popup \"{model.Title}\"", ex);
            _popups.Remove(popup);
            popup.Dispose();
            return false;
        }
    }

    public void DismissAll()
    {
        _closingAll = true;
        try
        {
            foreach (AlertPopupForm popup in _popups.ToList())
            {
                if (!popup.IsDisposed)
                {
                    popup.Close();
                }
            }

            _popups.Clear();
            if (_pill is { IsDisposed: false })
            {
                _pill.Hide();
            }
        }
        finally
        {
            _closingAll = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DismissAll();
        _pill?.Dispose();
        _pill = null;
        _disposed = true;
    }

    private void OnPopupClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is AlertPopupForm popup)
        {
            popup.FormClosed -= OnPopupClosed;
            popup.WorkAreaChanged -= Relayout;
            _popups.Remove(popup);
        }

        Relayout();
    }

    /// <summary>
    /// Positions every popup for the current work area; hides the ones that do not fit. Moving a window
    /// can make Windows send WM_DPICHANGED synchronously, and the popup then resizes and asks for a new
    /// layout from inside this one; that request is run after the current pass instead of nested in it,
    /// where it would be overwritten with positions computed from the old sizes.
    /// </summary>
    private void Relayout()
    {
        if (_disposed || _closingAll)
        {
            return;
        }

        if (_arranging)
        {
            _arrangeAgain = true;
            return;
        }

        _arranging = true;
        try
        {
            // Bounded, so monitors that keep handing a popup back and forth cannot loop forever.
            for (int pass = 0; pass < 3; pass++)
            {
                _arrangeAgain = false;
                Arrange();
                if (!_arrangeAgain || _disposed || _closingAll)
                {
                    break;
                }
            }
        }
        finally
        {
            _arranging = false;
        }
    }

    private void Arrange()
    {
        try
        {
            _popups.RemoveAll(p => p.IsDisposed);
            if (_popups.Count == 0)
            {
                if (_pill is { IsDisposed: false })
                {
                    _pill.Hide();
                }

                return;
            }

            Rectangle work = SystemInformation.WorkingArea;
            int dpi = _popups[^1].DeviceDpi;
            List<AlertPopupForm> newestFirst = Enumerable.Reverse(_popups).ToList();
            MorePillForm pill = EnsurePill(work);
            Size pillSize = pill.SetCount(Math.Max(1, newestFirst.Count - AlertStackLayout.MaxVisible), dpi);
            AlertStackPlacement placement = AlertStackLayout.Compute(
                work,
                newestFirst.ConvertAll(p => p.Size),
                pillSize,
                Scale(Margin, dpi),
                Scale(Spacing, dpi));

            for (int i = 0; i < newestFirst.Count; i++)
            {
                if (placement.Locations[i] is Point location)
                {
                    newestFirst[i].Location = location;
                    newestFirst[i].SetOnScreen(true);
                }
                else
                {
                    newestFirst[i].SetOnScreen(false);
                }
            }

            if (placement is { HiddenCount: > 0, PillLocation: Point pillLocation })
            {
                Size size = pill.SetCount(placement.HiddenCount, dpi);
                pill.Location = new Point(work.Right - Scale(Margin, dpi) - size.Width, pillLocation.Y);
                pill.ShowNoActivate();
            }
            else
            {
                pill.Hide();
            }
        }
        catch (ExternalException ex)
        {
            Log.Error("Could not arrange the popups", ex);
        }
    }

    private MorePillForm EnsurePill(Rectangle work)
    {
        if (_pill is { IsDisposed: false })
        {
            return _pill;
        }

        _pill = new MorePillForm
        {
            Bounds = new Rectangle(work.Right - 2, work.Bottom - 2, 1, 1),
            FormScreenCaptureMode = HideFromScreenCapture ? ScreenCaptureMode.HideWindow : ScreenCaptureMode.Allow,
        };
        _pill.WorkAreaChanged += Relayout;
        return _pill;
    }

    private static int Scale(int logical, int dpi) => (int)Math.Round(logical * dpi / 96f);
}
