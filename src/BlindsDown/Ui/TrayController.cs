using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;
using BlindsDown.Ui.Tray;
using Microsoft.Win32;

namespace BlindsDown.Ui;

/// <summary>
/// The notification-area (tray) icon and its menu. Left click toggles the lock (a fallback
/// for when the hotkey is swallowed by an elevated or fullscreen app). Create and use on the UI thread.
/// Icon is drawn at the exact tray size for the current DPI and taskbar theme, and is re-rendered on
/// DPI/theme/display changes. The locked icon differs in SHAPE as well as colour.
/// Menu: Lock/Unlock (shows hotkey), Activity..., Settings..., About BlindsDown, Exit.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private readonly TrayMenuLine _headline;
    private readonly TrayMenuLine _problemLine;
    private readonly TrayMenuLine[] _usageLines;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIconWatchdog _watchdog;
    private readonly TrayClickFilter _clicks = new(SystemInformation.DoubleClickTime);

    /// <summary>The WinForms context of the UI thread; SystemEvents may call us on another thread.</summary>
    private readonly SynchronizationContext? _ui;

    private LockStatusInfo _status = LockStatusInfo.Unlocked;
    private bool _cameraLockOn;
    private Hotkey _hotkey = Hotkey.Default;
    private IReadOnlyList<UsageSession> _active = [];
    private Icon? _icon;
    private (LockState State, int Size, bool Dark)? _iconKey;
    private Font? _boldFont;
    private bool _disposed;

    public TrayController()
    {
        _headline = new TrayMenuLine { ImageScaling = ToolStripItemImageScaling.None };
        _problemLine = new TrayMenuLine { Visible = false };
        _usageLines = [new TrayMenuLine { Visible = false }, new TrayMenuLine { Visible = false }];
        _toggleItem = new ToolStripMenuItem(TrayText.ToggleLabel(LockState.Unlocked), null, (_, _) => Raise(ToggleRequested, "lock toggle"));

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_headline);
        _menu.Items.Add(_problemLine);
        _menu.Items.AddRange(_usageLines);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("&Activity…", null, (_, _) => Raise(ActivityRequested, "activity")));
        _menu.Items.Add(new ToolStripMenuItem("&Settings…", null, (_, _) => Raise(SettingsRequested, "settings")));
        _menu.Items.Add(new ToolStripMenuItem("A&bout BlindsDown", null, (_, _) => Raise(AboutRequested, "about")));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Raise(ExitRequested, "exit")));
        _menu.Opening += (_, _) => RefreshMenu();

        // Creating the menu (a Control) installed the WinForms synchronization context on this thread.
        _ui = SynchronizationContext.Current;

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        _watchdog = new TrayIconWatchdog(_notifyIcon);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshIcon();
        RefreshTooltip();
        RefreshMenu();
    }

    public event Action? ToggleRequested;

    public event Action? ActivityRequested;

    public event Action? SettingsRequested;

    public event Action? AboutRequested;

    public event Action? ExitRequested;

    /// <summary>The tray menu, so tests can check and render its status lines without opening it.</summary>
    internal ContextMenuStrip Menu => _menu;

    /// <summary>Shows the icon. Call once after construction.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        RefreshIcon();
        RefreshTooltip();
        _notifyIcon.Visible = true;
        _watchdog.Start();
    }

    /// <summary>
    /// Updates icon, tooltip and menu to match the lock state. <paramref name="status"/> may include the camera
    /// helper's devices: while a camera is turned off (<see cref="DeviceProtection.DeviceDisabled"/>) the menu
    /// header and tooltip say "Microphones and cameras locked". <paramref name="cameraLockOn"/>: the helper works
    /// and "Lock cameras" is on, so while unlocked the menu offers "Lock microphones and cameras".
    /// </summary>
    public void SetStatus(LockStatusInfo status, Hotkey hotkey, bool cameraLockOn = false)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_disposed)
        {
            return;
        }

        _status = status;
        _hotkey = hotkey;
        _cameraLockOn = cameraLockOn;
        RefreshIcon();
        RefreshTooltip();
        RefreshMenu();
    }

    /// <summary>Updates the tooltip / menu header with who is using the mic or camera right now.</summary>
    public void SetActiveUsage(IReadOnlyList<UsageSession> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        if (_disposed)
        {
            return;
        }

        _active = [.. active];
        RefreshTooltip();
        RefreshMenu();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watchdog.Dispose();

        // Hiding sends NIM_DELETE right away, so no ghost icon stays behind in the notification area.
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Dispose();

        Image? headlineImage = _headline.Image;
        _menu.Dispose();
        headlineImage?.Dispose();
        _boldFont?.Dispose();
        _icon?.Dispose();

        ToggleRequested = null;
        ActivityRequested = null;
        SettingsRequested = null;
        AboutRequested = null;
        ExitRequested = null;
    }

    /// <summary>The small-icon size of the primary monitor, where the notification area lives.</summary>
    private static int TrayIconSize()
    {
        nint primary = TrayNative.MonitorFromPoint(default, TrayNative.MonitorDefaultToPrimary);
        uint dpi = TrayNative.GetMonitorDpi(primary);
        int size = TrayNative.GetSystemMetricsForDpi(TrayNative.SmCxSmIcon, dpi);
        return size > 0 ? size : (int)Math.Round(16 * dpi / 96.0);
    }

    private static void Raise(Action? handler, string what)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            // A failing menu action must never take the tray (and with it the way to unlock) down.
            Log.Error($"Tray {what} action failed", ex);
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _clicks.OnLeftMouseUp(Environment.TickCount64))
        {
            Raise(ToggleRequested, "lock toggle");
        }
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _clicks.OnDoubleClick();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => PostToUi(RefreshIcon);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => PostToUi(RefreshIcon);

    /// <summary>
    /// SystemEvents raises on the context captured at subscription, which is normally ours, but posting
    /// also keeps the work out of the middle of a WM_SETTINGCHANGE broadcast.
    /// </summary>
    private void PostToUi(Action action)
    {
        if (_ui is null || _disposed)
        {
            return;
        }

        _ui.Post(
            _ =>
            {
                if (!_disposed)
                {
                    action();
                }
            },
            null);
    }

    private void RefreshIcon()
    {
        try
        {
            int size = TrayIconSize();
            bool dark = Theme.TaskbarIsDark();
            (LockState, int, bool) key = (_status.State, size, dark);
            if (_icon is not null && _iconKey == key)
            {
                return;
            }

            Icon next = TrayIconRenderer.Render(_status.State, size, dark);
            Icon? previous = _icon;
            _icon = next;
            _iconKey = key;

            // The shell copies the HICON during NIM_ADD/NIM_MODIFY, so the old icon can go right away.
            _notifyIcon.Icon = next;
            previous?.Dispose();
        }
        catch (Exception ex) when (IsDrawingFailure(ex))
        {
            // Runs from posted SystemEvents callbacks too, where an escaping exception would reach
            // WinForms' crash dialog; the previous icon simply stays.
            Log.Error("Could not draw the tray icon", ex);
        }
    }

    private void RefreshTooltip() => _notifyIcon.Text = TrayText.Tooltip(_status, _active, _cameraLockOn);

    private void RefreshMenu()
    {
        LockState state = _status.State;
        bool cameras = TrayText.MentionsCameras(_status, _cameraLockOn);
        EnsureBoldFont();
        _headline.Text = TrayText.Headline(state, cameras);
        SetHeadlineImage(state);

        string? problem = TrayText.ProblemMenuLine(_status);
        _problemLine.Text = EscapeMnemonics(problem ?? string.Empty);
        _problemLine.Visible = problem is not null;

        IReadOnlyList<string> usage = TrayText.UsageMenuLines(_active);
        for (int i = 0; i < _usageLines.Length; i++)
        {
            _usageLines[i].Visible = i < usage.Count;
            _usageLines[i].Text = i < usage.Count ? EscapeMnemonics(usage[i]) : string.Empty;
        }

        _toggleItem.Text = TrayText.ToggleLabel(state, cameras);
        _toggleItem.ShortcutKeyDisplayString = _hotkey.ToString();
        _toggleItem.Enabled = state is not (LockState.Locking or LockState.Unlocking);
    }

    /// <summary>The menu font can change with the DPI of the monitor it opens on; follow it.</summary>
    private void EnsureBoldFont()
    {
        Font baseFont = _menu.Font;
        if (_boldFont is not null && _boldFont.Size.Equals(baseFont.Size) && _boldFont.FontFamily.Equals(baseFont.FontFamily))
        {
            return;
        }

        Font? previous = _boldFont;
        _boldFont = new Font(baseFont, FontStyle.Bold);
        _headline.Font = _boldFont;
        previous?.Dispose();
    }

    private void SetHeadlineImage(LockState state)
    {
        try
        {
            int size = Math.Max(TrayIconRenderer.MinSize, (int)Math.Round(16 * _menu.DeviceDpi / 96.0));
            bool darkMenu = _menu.BackColor.GetBrightness() < 0.5f;
            Image? previous = _headline.Image;
            _headline.Image = TrayIconRenderer.RenderBitmap(state, size, darkMenu);
            previous?.Dispose();
        }
        catch (Exception ex) when (IsDrawingFailure(ex))
        {
            Log.Error("Could not draw the menu status image", ex);
        }
    }

    /// <summary>
    /// GDI+ reports failures as ExternalException (generic/Win32 errors), ArgumentException ("Parameter is
    /// not valid", also used when memory runs short) or InvalidOperationException (object busy). Same set
    /// as the lock overlay and the OSD flash.
    /// </summary>
    private static bool IsDrawingFailure(Exception ex) =>
        ex is ExternalException or ArgumentException or InvalidOperationException;

    /// <summary>App names can contain "&amp;", which a menu would turn into an underlined access key.</summary>
    private static string EscapeMnemonics(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}
