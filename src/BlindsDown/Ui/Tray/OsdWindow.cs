using BlindsDown.Core.Locking;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Tray;

/// <summary>
/// The OSD flash window: painted normally by WinForms, made translucent and faded with
/// <see cref="Form.Opacity"/> (SetLayeredWindowAttributes). It must never be mixed with
/// UpdateLayeredWindow: once a window has used one of the two, the other fails.
///
/// Quirks handled here:
/// - Opacity stays below 1.0 for the window's whole life. WinForms only keeps WS_EX_LAYERED (and calls
///   SetLayeredWindowAttributes) below 100%, and click-through (WS_EX_TRANSPARENT) needs a layered window.
/// - Rounded corners come from DWM (Form.FormCornerPreference, Windows 11; square elsewhere).
/// - WM_DPICHANGED is swallowed: <see cref="OsdFlash"/> sizes the window for its monitor itself.
/// </summary>
internal sealed class OsdWindow : Form
{
    public const double MaxOpacity = 0.96;

    private LockState _state;
    private string? _detail;
    private float _scale = 1f;
    private bool _dark;
    private bool _cameras;

    public OsdWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "BlindsDown";
        Opacity = MaxOpacity;
        FormCornerPreference = FormCornerPreference.Round;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

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

    /// <summary>Shows (or replaces) the content at <paramref name="bounds"/> (physical pixels), fully opaque again.</summary>
    public void ShowContent(LockState state, string? detail, float scale, Rectangle bounds, bool hideFromCapture, bool cameras = false)
    {
        FormScreenCaptureMode = hideFromCapture ? ScreenCaptureMode.HideWindow : ScreenCaptureMode.Allow;
        _state = state;
        _detail = detail;
        _scale = scale;
        _cameras = cameras;
        _dark = Theme.AppsUseDarkMode();
        BackColor = _dark ? Theme.DarkSurface : Theme.LightSurface;
        FormBorderColor = OverlayPainter.StateColor(state); // Windows 11 draws a 1 px border in this colour
        Opacity = MaxOpacity;
        Bounds = bounds;
        Invalidate();
        if (!Visible)
        {
            Show();
        }

        _ = TrayNative.SetWindowPos(
            Handle,
            TrayNative.HwndTopmost,
            0,
            0,
            0,
            0,
            TrayNative.SwpNoMove | TrayNative.SwpNoSize | TrayNative.SwpNoActivate);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        OverlayPainter.DrawOsd(e.Graphics, ClientRectangle, _state, _detail, _scale, _dark, _cameras);
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
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }
}
