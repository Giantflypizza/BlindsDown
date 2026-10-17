using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

public class TrayIconWatchdogTests
{
    /// <summary>
    /// Guards the reflection the logon workaround depends on: if a WinForms update renames NotifyIcon's
    /// private fields, this fails loudly instead of the check silently doing nothing in the field.
    /// The icon is never made visible, so nothing appears in the notification area.
    /// </summary>
    [Fact]
    public void NotifyIconIdentityIsReadableInThisWinFormsVersion()
    {
        using var icon = new NotifyIcon();
        Assert.True(TrayIconWatchdog.TryGetIdentity(icon, out nint hwnd, out uint id));
        Assert.True(id > 0);
        Assert.Equal(0, hwnd); // no window until the icon is shown

        Assert.Null(TrayIconWatchdog.IsKnownToShell(icon)); // can't ask the shell without a window
    }
}
