using System.Reflection;
using System.Runtime.InteropServices;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Tray;

/// <summary>
/// Works around a WinForms gap: NotifyIcon ignores the result of Shell_NotifyIcon(NIM_ADD). At logon
/// Explorer may already exist but not yet accept icons, the add fails silently, WinForms believes the
/// icon is there and only ever sends NIM_MODIFY afterwards, and no TaskbarCreated message follows to fix
/// it. So a few times over the first ~30 s we ask the shell whether it knows the icon
/// (Shell_NotifyIconGetRect) and, if not, re-add it by toggling Visible.
///
/// The icon's owner window and id are private NotifyIcon fields; they are read by reflection and, if a
/// future WinForms renames them, the check is simply skipped (logged once). Bounded to three re-adds,
/// so a shell that answers "unknown" for icons hidden in the overflow area can't cause churn.
/// </summary>
internal sealed class TrayIconWatchdog : IDisposable
{
    private static readonly int[] CheckDelaysMs = [3_000, 10_000, 30_000];

    private static readonly FieldInfo? IdField =
        typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? WindowField =
        typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer = new();
    private int _step;

    public TrayIconWatchdog(NotifyIcon icon)
    {
        _icon = icon;
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _step = 0;
        ScheduleNext();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
    }

    /// <summary>True/false when the shell could be asked; null when the icon's identity is not available.</summary>
    internal static bool? IsKnownToShell(NotifyIcon icon)
    {
        if (!TryGetIdentity(icon, out nint hwnd, out uint id) || hwnd == 0)
        {
            return null;
        }

        var identifier = new TrayNative.NotifyIconIdentifier
        {
            CbSize = (uint)Marshal.SizeOf<TrayNative.NotifyIconIdentifier>(),
            HWnd = hwnd,
            UId = id,
        };
        return TrayNative.Shell_NotifyIconGetRect(in identifier, out _) >= 0;
    }

    /// <summary>Reads NotifyIcon's private owner window and id (see the class comment).</summary>
    internal static bool TryGetIdentity(NotifyIcon icon, out nint hwnd, out uint id)
    {
        hwnd = 0;
        id = 0;
        if (IdField?.GetValue(icon) is not uint rawId || WindowField?.GetValue(icon) is not NativeWindow window)
        {
            return false;
        }

        id = rawId;
        hwnd = window.Handle;
        return true;
    }

    private void ScheduleNext()
    {
        if (_step >= CheckDelaysMs.Length)
        {
            return;
        }

        int previous = _step == 0 ? 0 : CheckDelaysMs[_step - 1];
        _timer.Interval = CheckDelaysMs[_step] - previous;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        try
        {
            if (!_icon.Visible)
            {
                return;
            }

            bool? known = IsKnownToShell(_icon);
            if (known is null)
            {
                Log.Warn("Tray icon check skipped: NotifyIcon internals not available in this WinForms version.");
                return;
            }

            if (known == false)
            {
                Log.Warn($"The shell does not know the tray icon (check {_step + 1}); adding it again.");
                _icon.Visible = false;
                _icon.Visible = true;
            }
        }
        catch (ExternalException ex)
        {
            Log.Error("Tray icon check failed", ex);
        }

        _step++;
        ScheduleNext();
    }
}
