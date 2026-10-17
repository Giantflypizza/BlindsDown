using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Tray;

/// <summary>
/// One piece of the lock indicator (a pill or a screen-edge line): a click-through, never-activated,
/// topmost layered window whose content is a per-pixel-alpha bitmap pushed with UpdateLayeredWindow,
/// which gives antialiased rounded edges on every Windows version (a window Region would be jagged).
///
/// Quirks handled here:
/// - WS_EX_LAYERED without SetLayeredWindowAttributes/UpdateLayeredWindow is invisible, so content is
///   always pushed before the window is shown.
/// - WS_EX_TRANSPARENT only makes a window click-through when it is also layered; WM_NCHITTEST and
///   WM_MOUSEACTIVATE are answered too, so nothing can ever activate it.
/// - The app is per-monitor DPI aware: WinForms would rescale the window on WM_DPICHANGED and stretch
///   the bitmap. The manager sizes everything per monitor itself, so the message is swallowed and only
///   reported via <see cref="MonitorDpiChanged"/>.
/// - Form.TopMost is not used: its setter calls SetWindowPos without SWP_NOACTIVATE.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private readonly Rectangle _target;

    public OverlayWindow(Rectangle bounds, bool hideFromCapture)
    {
        _target = bounds;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "BlindsDown lock indicator";
        Bounds = bounds;

        // WinForms applies this with SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) when the handle is created.
        FormScreenCaptureMode = hideFromCapture ? ScreenCaptureMode.HideWindow : ScreenCaptureMode.Allow;
    }

    /// <summary>Raised when Windows reports a DPI change for this window (e.g. scaling changed).</summary>
    public event Action? MonitorDpiChanged;

    /// <summary>The window rectangle in physical pixels.</summary>
    public Rectangle Target => _target;

    /// <summary>True when Windows confirms the window is excluded from screenshots and screen sharing.</summary>
    internal bool IsExcludedFromCapture =>
        IsHandleCreated
        && TrayNative.GetWindowDisplayAffinity(Handle, out uint affinity) != 0
        && affinity == TrayNative.WdaExcludeFromCapture;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= TrayNative.WsExLayered | TrayNative.WsExTransparent | TrayNative.WsExToolWindow
                | TrayNative.WsExNoActivate | TrayNative.WsExTopmost;
            return cp;
        }
    }

    /// <summary>Replaces the window content (premultiplied per-pixel alpha) and moves it to <see cref="Target"/>.</summary>
    public unsafe bool Present(Bitmap content, byte opacity)
    {
        ArgumentNullException.ThrowIfNull(content);
        nint screenDc = TrayNative.GetDC(0);
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memoryDc = TrayNative.CreateCompatibleDC(screenDc);

            // A transparent background makes GDI+ produce a premultiplied 32-bpp DIB, as AC_SRC_ALPHA expects.
            bitmap = content.GetHbitmap(Color.FromArgb(0));
            previous = TrayNative.SelectObject(memoryDc, bitmap);

            var position = new TrayNative.Point { X = _target.Left, Y = _target.Top };
            var size = new TrayNative.Size { Cx = content.Width, Cy = content.Height };
            var source = default(TrayNative.Point);
            var blend = Blend(opacity);
            bool ok = TrayNative.UpdateLayeredWindow(Handle, screenDc, &position, &size, memoryDc, &source, 0, &blend, TrayNative.UlwAlpha) != 0;
            if (!ok)
            {
                Log.Warn($"UpdateLayeredWindow failed with error {TrayNative.GetLastError()}.");
            }

            return ok;
        }
        finally
        {
            if (previous != 0)
            {
                TrayNative.SelectObject(memoryDc, previous);
            }

            if (bitmap != 0)
            {
                TrayNative.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                TrayNative.DeleteDC(memoryDc);
            }

            _ = TrayNative.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>Changes only the overall opacity, keeping the current content.</summary>
    public unsafe void SetOpacity(byte opacity)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var blend = Blend(opacity);
        _ = TrayNative.UpdateLayeredWindow(Handle, 0, null, null, 0, null, 0, &blend, TrayNative.UlwAlpha);
    }

    /// <summary>Puts the window back at the top of the topmost band without activating it.</summary>
    public void ReassertTopmost()
    {
        if (IsHandleCreated)
        {
            _ = TrayNative.SetWindowPos(
                Handle,
                TrayNative.HwndTopmost,
                0,
                0,
                0,
                0,
                TrayNative.SwpNoMove | TrayNative.SwpNoSize | TrayNative.SwpNoActivate);
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case TrayNative.WmNcHitTest:
                m.Result = TrayNative.HtTransparent;
                return;
            case TrayNative.WmMouseActivate:
                m.Result = TrayNative.MaNoActivate;
                return;
            case TrayNative.WmDpiChanged:
                MonitorDpiChanged?.Invoke();
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }

    /// <summary>The content comes from UpdateLayeredWindow; GDI painting would be ignored anyway.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MonitorDpiChanged = null;
        }

        base.Dispose(disposing);
    }

    private static TrayNative.BlendFunction Blend(byte opacity) => new()
    {
        BlendOp = TrayNative.AcSrcOver,
        SourceConstantAlpha = opacity,
        AlphaFormat = TrayNative.AcSrcAlpha,
    };
}
