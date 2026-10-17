namespace BlindsDown.Ui.Tray;

/// <summary>
/// Turns tray mouse-ups into "toggle" decisions. A double-click arrives from the shell as
/// down, up, DBLCLK, up: both ups raise NotifyIcon.MouseUp, and toggling on each would lock and
/// immediately unlock again. So the up that follows a double-click is swallowed, and as a second
/// line of defence any left-up within the double-click time of the last toggle is ignored too.
/// </summary>
internal sealed class TrayClickFilter(int doubleClickMs)
{
    private bool _swallowNextUp;
    private long? _lastToggleMs;

    /// <summary>Returns true when this mouse-up should toggle the lock.</summary>
    public bool OnLeftMouseUp(long nowMs)
    {
        if (_swallowNextUp)
        {
            _swallowNextUp = false;
            return false;
        }

        if (_lastToggleMs is long last && nowMs - last >= 0 && nowMs - last < doubleClickMs)
        {
            return false;
        }

        _lastToggleMs = nowMs;
        return true;
    }

    public void OnDoubleClick() => _swallowNextUp = true;
}
