using System.Runtime.InteropServices;

namespace BlindsDown.Ui.Native;

/// <summary>
/// Win32 calls used by the always-visible UI (tray icon, hotkey, lock overlay, OSD flash, sounds).
/// Only allowlisted system DLLs; blittable signatures (BOOL is returned as int) so the source-generated
/// stubs stay trivial. Named after the component so it never collides with other agents' interop types.
/// </summary>
internal static unsafe partial class TrayNative
{
    public const int WmHotkey = 0x0312;
    public const int WmDpiChanged = 0x02E0;
    public const int WmNcHitTest = 0x0084;
    public const int WmMouseActivate = 0x0021;
    public const int HtTransparent = -1;
    public const int MaNoActivate = 3;

    public const uint ModNoRepeat = 0x4000;

    public const int ErrorHotkeyAlreadyRegistered = 1409;
    public const int ErrorInvalidParameter = 87;

    public const int SmCxSmIcon = 49;

    public const int WsExTopmost = 0x00000008;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;

    public const nint HwndTopmost = -1;
    public const nint HwndMessage = -3;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    public const uint MonitorDefaultToNearest = 2;
    public const uint MonitorDefaultToPrimary = 1;
    public const int MdtEffectiveDpi = 0;

    public const uint EventSystemForeground = 0x0003;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;

    public const uint WdaExcludeFromCapture = 0x00000011;

    public const uint UlwAlpha = 0x00000002;
    public const byte AcSrcOver = 0x00;
    public const byte AcSrcAlpha = 0x01;

    public const uint SndAsync = 0x0001;
    public const uint SndNoDefault = 0x0002;
    public const uint SndMemory = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    /// <summary>NOTIFYICONIDENTIFIER: identifies a tray icon by owner window + id (we never use GUID icons:
    /// those are bound to the exe path, which breaks a portable exe that is moved).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconIdentifier
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public Guid GuidItem;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int UnregisterHotKey(nint hWnd, int id);

    /// <summary>Windows 10 1607+. Returns 0 on failure (no extended error information).</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(Point pt, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial int UnhookWinEvent(nint hWinEventHook);

    /// <summary>Reads back what Form.FormScreenCaptureMode applied (WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004+).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDc);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int UpdateLayeredWindow(
        nint hWnd,
        nint hdcDst,
        Point* pptDst,
        Size* psize,
        nint hdcSrc,
        Point* pptSrc,
        uint crKey,
        BlendFunction* pblend,
        uint dwFlags);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteDC(nint hdc);

    /// <summary>Returns an HRESULT. Fails (E_FAIL) when the shell does not know the icon, which is how a
    /// silently failed NIM_ADD (Explorer not ready at logon) can be detected.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int Shell_NotifyIconGetRect(in NotifyIconIdentifier identifier, out Rect iconLocation);

    /// <summary>Windows 8.1+. Returns an HRESULT.</summary>
    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Plays a WAV image from memory. With SND_ASYNC, winmm keeps reading <paramref name="pszSound"/> on its
    /// own thread after this returns, so the buffer must never move or be freed while a sound plays.
    /// </summary>
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    public static partial int PlaySound(byte* pszSound, nint hmod, uint fdwSound);

    public static int GetLastError() => Marshal.GetLastPInvokeError();

    /// <summary>Effective DPI of a monitor (96 = 100%), or 96 if Windows can't tell.</summary>
    public static uint GetMonitorDpi(nint hmonitor)
    {
        if (hmonitor == 0)
        {
            return 96;
        }

        int hr = GetDpiForMonitor(hmonitor, MdtEffectiveDpi, out uint dpiX, out _);
        return hr >= 0 && dpiX >= 48 ? dpiX : 96;
    }
}
