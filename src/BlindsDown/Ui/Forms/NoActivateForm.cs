using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// Base for windows that float above everything but must never take focus from the app the user is
/// typing in (popups, the "+N more" pill). Three things are needed together:
///  * <see cref="ShowWithoutActivation"/>: Show() uses SW_SHOWNOACTIVATE;
///  * WS_EX_NOACTIVATE: clicks do not activate the window (and WM_MOUSEACTIVATE answers MA_NOACTIVATE
///    as a second line of defence), yet mouse messages still arrive, so buttons work;
///  * WS_EX_TOPMOST set at creation instead of <see cref="Form.TopMost"/>, whose setter calls
///    SetWindowPos without SWP_NOACTIVATE.
/// WS_EX_TOOLWINDOW keeps the window out of Alt+Tab and the taskbar. Content is owner-drawn: child
/// controls would grab focus (and activate the window) when clicked.
/// </summary>
internal abstract class NoActivateForm : Form
{
    protected NoActivateForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>The primary screen's work area may have changed (taskbar moved or resized, display change).
    /// Top-level windows receive these broadcasts directly, so no SystemEvents subscription is needed.</summary>
    public event Action? WorkAreaChanged;

    /// <summary>True when Windows 11 rounds the corners (and draws the outline) for us.</summary>
    protected bool HasSystemFrame { get; private set; }

    /// <summary>Corner style requested from DWM: small for the pill, normal for popups.</summary>
    protected virtual bool SmallCorners => false;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= PopupNative.WS_EX_NOACTIVATE | PopupNative.WS_EX_TOOLWINDOW | PopupNative.WS_EX_TOPMOST;
            cp.ClassStyle |= PopupNative.CS_DROPSHADOW;
            return cp;
        }
    }

    /// <summary>Shows the window (if hidden) without activating it and keeps it above other topmost windows.</summary>
    public void ShowNoActivate()
    {
        if (!Visible)
        {
            Show();
        }

        PopupNative.BringToTopNoActivate(Handle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HasSystemFrame = PopupNative.TryRoundCorners(Handle, SmallCorners);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case PopupNative.WM_MOUSEACTIVATE:
                m.Result = PopupNative.MA_NOACTIVATE;
                return;
            case PopupNative.WM_DISPLAYCHANGE:
            case PopupNative.WM_SETTINGCHANGE when m.WParam == PopupNative.SPI_SETWORKAREA:
                base.WndProc(ref m);

                // Posted, not called: other windows (and WinForms' own screen cache) are still
                // processing the same broadcast.
                BeginInvoke(() => WorkAreaChanged?.Invoke());
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }
}
