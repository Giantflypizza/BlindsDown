using System.Runtime.InteropServices;

namespace BlindsDown.Ui.Native;

/// <summary>
/// The few Win32 calls the popups and dialogs need that WinForms does not expose: rounded corners on
/// Windows 11, re-asserting "topmost" without activating, and reading the Win key state for the
/// hotkey capture box.
/// </summary>
internal static partial class PopupNative
{
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int CS_DROPSHADOW = 0x00020000;

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int MA_NOACTIVATE = 3;

    /// <summary>wParam of WM_SETTINGCHANGE when the work area changed (taskbar moved, resized or auto-hidden).</summary>
    public const int SPI_SETWORKAREA = 0x002F;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    private const nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>
    /// Asks Windows 11 to round the window's corners (DWMWA_WINDOW_CORNER_PREFERENCE). Borderless
    /// windows are square by default. Returns false on Windows 10, which does not know the attribute
    /// (it returns E_INVALIDARG); callers then draw their own square border instead.
    /// </summary>
    public static bool TryRoundCorners(nint hwnd, bool small)
    {
        int preference = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) >= 0;
    }

    /// <summary>
    /// Moves the window to the top of the topmost band WITHOUT activating it. Form.TopMost would call
    /// SetWindowPos without SWP_NOACTIVATE, which can steal focus from the app the user is typing in.
    /// </summary>
    public static void BringToTopNoActivate(nint hwnd) =>
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>
    /// True while either Windows key is held. WinForms' ModifierKeys has no Win flag, so the hotkey box
    /// reads the key state directly (high bit set = down).
    /// </summary>
    public static bool IsWinKeyDown() =>
        (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
