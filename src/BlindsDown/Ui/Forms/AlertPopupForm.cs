using BlindsDown.Infrastructure;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// One owner-drawn popup (see <see cref="AlertPopupManager"/> for how they are stacked). It never takes
/// focus, auto-dismisses after <see cref="AutoDismissAfter"/> unless sticky, and pauses that countdown
/// while the mouse is over it. Close and action buttons are painted, not child controls, so clicking
/// them cannot activate the window.
/// </summary>
internal sealed class AlertPopupForm : NoActivateForm
{
    private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(8);

    /// <summary>After the mouse leaves, at least this much time remains so the user can move back.</summary>
    private static readonly TimeSpan MinimumAfterHover = TimeSpan.FromSeconds(2);

    private readonly System.Windows.Forms.Timer _dismissTimer = new();
    private AlertPopupLayout? _layout;
    private PopupHit _hover;
    private PopupHit _pressed;
    private bool _sticky;
    private bool _onScreen;
    private bool _mouseInside;
    private TimeSpan _remaining = AutoDismissAfter;
    private long _countdownStartedAt;

    public AlertPopupForm(AlertModel model)
    {
        Model = model;
        Key = KeyFor(model);
        _sticky = model.Sticky;
        Text = model.Title;
        AccessibleName = model.Title;
        AccessibleDescription = model.Message;
        AccessibleRole = AccessibleRole.Alert;
        _dismissTimer.Tick += (_, _) => Close();
    }

    /// <summary>Raised when the popup's size changed (DPI change), so the stack must be rearranged.</summary>
    public event Action<AlertPopupForm>? SizeInvalidated;

    public AlertModel Model { get; private set; }

    public string Key { get; }

    /// <summary>Identity used to merge repeats: the group key if the alert has one, else title + message.</summary>
    public static string KeyFor(AlertModel model) =>
        model.GroupKey is { } group ? "group" + group : AlertCoalescer.KeyOf(model.Title, model.Message);

    /// <summary>The same kind of alert arrived again with new text: show the new text in this popup and
    /// restart its countdown (sticky if either version is sticky).</summary>
    public void UpdateContent(AlertModel model)
    {
        Model = model with { Sticky = model.Sticky || _sticky };
        Text = model.Title;
        AccessibleName = model.Title;
        AccessibleDescription = model.Message;
        if (IsHandleCreated)
        {
            Rebuild(DeviceDpi);
            SizeInvalidated?.Invoke(this);
        }

        Renew(model.Sticky);
        Announce();
    }

    /// <summary>Creates the window on the primary screen and sizes it for that screen's DPI.</summary>
    public void Prepare(Point initialLocation)
    {
        Bounds = new Rectangle(initialLocation, new Size(1, 1));
        Rebuild(DpiOfNewHandle());
    }

    /// <summary>Shows or hides the popup (hidden ones wait in the "+N more" overflow). The countdown only runs while shown.</summary>
    public void SetOnScreen(bool onScreen)
    {
        _onScreen = onScreen;
        if (onScreen)
        {
            ShowNoActivate();
        }
        else if (Visible)
        {
            Hide();
        }

        UpdateCountdown();
    }

    /// <summary>The same alert arrived again: start the countdown over (and keep it if the repeat is sticky).</summary>
    public void Renew(bool sticky)
    {
        _sticky |= sticky;
        _remaining = AutoDismissAfter;
        _dismissTimer.Stop();
        UpdateCountdown();
    }

    /// <summary>Tells screen readers about the popup; it never gets focus, so they would not notice it otherwise.</summary>
    public void Announce()
    {
        string text = string.IsNullOrWhiteSpace(Model.Message) ? Model.Title : Model.Title + ". " + Model.Message;
        _ = AccessibilityObject.RaiseAutomationNotification(
            System.Windows.Forms.Automation.AutomationNotificationKind.Other,
            System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
            text);
    }

    /// <summary>Paints the popup into a bitmap at any DPI (visual checks; the window itself paints the same way).</summary>
    public static Bitmap Render(AlertModel model, int dpi, bool drawOutline)
    {
        using AlertPopupLayout layout = AlertPopupLayout.Create(model, dpi);

        // 24 bpp: GDI text leaves the alpha channel at 0 on 32 bpp bitmaps, which garbles it in a PNG.
        var bitmap = new Bitmap(layout.Size.Width, layout.Size.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(bitmap);
        layout.Paint(g, default, default, drawOutline);
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_layout is null)
        {
            return;
        }

        _layout.Paint(e.Graphics, _hover, _pressed, drawOutline: !HasSystemFrame);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // The popup lays itself out from the DPI; WinForms' scaling would fight that.
        e.Cancel = true;
        base.OnDpiChanged(e);
        Rebuild(e.DeviceDpiNew);
        SizeInvalidated?.Invoke(this);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _mouseInside = true;
        UpdateCountdown();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        SetHover(default);
        _pressed = default;
        UpdateCountdown();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout is null)
        {
            return;
        }

        PopupHit hit = _layout.HitTest(e.Location);
        SetHover(hit);
        Cursor = hit.Part is PopupPart.Close or PopupPart.Action ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _layout is not null)
        {
            _pressed = _layout.HitTest(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _layout is null)
        {
            return;
        }

        PopupHit released = _layout.HitTest(e.Location);
        PopupHit pressed = _pressed;
        _pressed = default;
        Invalidate();
        if (released != pressed)
        {
            return;
        }

        Press(released);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PopupAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismissTimer.Dispose();
            _layout?.Dispose();
            _layout = null;
        }

        base.Dispose(disposing);
    }

    private void Press(PopupHit hit)
    {
        if (hit.Part == PopupPart.Action && Model.Actions is { } actions && hit.Index >= 0 && hit.Index < actions.Count)
        {
            AlertAction action = actions[hit.Index];

            // The popup closes after the action anyway. If the action runs a modal dialog, the countdown
            // must not close (and dispose) the popup underneath it in the meantime.
            _sticky = true;
            UpdateCountdown();
            try
            {
                action.OnClick();
            }
            catch (Exception ex)
            {
                // An action handler must never take the app down with it.
                Log.Error($"Popup action \"{action.Label}\" failed", ex);
            }
        }

        // The handler may already have closed every popup (e.g. "dismiss all").
        if ((hit.Part is PopupPart.Close or PopupPart.Action) && !IsDisposed)
        {
            Close();
        }
    }

    private int DpiOfNewHandle()
    {
        _ = Handle;
        return DeviceDpi;
    }

    private void Rebuild(int dpi)
    {
        AlertPopupLayout? old = _layout;
        _layout = AlertPopupLayout.Create(Model, dpi);
        old?.Dispose();

        // Grow up and to the left, keeping the bottom-right corner. The popup is created in the corner
        // of the primary work area; growing right and down would put most of it on a neighbouring
        // monitor, and if that one has another scale Windows would rescale the popup for it.
        Rectangle bounds = Bounds;
        Size size = _layout.Size;
        Bounds = new Rectangle(bounds.Right - size.Width, bounds.Bottom - size.Height, size.Width, size.Height);
        Invalidate();
    }

    private void SetHover(PopupHit hit)
    {
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
    }

    private void UpdateCountdown()
    {
        bool shouldRun = !_sticky && _onScreen && !_mouseInside && !IsDisposed;
        if (shouldRun && !_dismissTimer.Enabled)
        {
            TimeSpan wait = _remaining < MinimumAfterHover ? MinimumAfterHover : _remaining;
            _dismissTimer.Interval = (int)wait.TotalMilliseconds;
            _countdownStartedAt = Environment.TickCount64;
            _dismissTimer.Start();
        }
        else if (!shouldRun && _dismissTimer.Enabled)
        {
            _dismissTimer.Stop();
            _remaining -= TimeSpan.FromMilliseconds(Environment.TickCount64 - _countdownStartedAt);
        }
    }

    /// <summary>
    /// Exposes the painted buttons to screen readers and UI automation as real push buttons, since they
    /// are not child windows.
    /// </summary>
    private sealed class PopupAccessibleObject : ControlAccessibleObject
    {
        private readonly AlertPopupForm _owner;
        private readonly List<PopupButtonAccessibleObject> _children = [];

        public PopupAccessibleObject(AlertPopupForm owner)
            : base(owner)
        {
            _owner = owner;
        }

        public override AccessibleRole Role => AccessibleRole.Alert;

        public override int GetChildCount()
        {
            EnsureChildren();
            return _children.Count;
        }

        public override AccessibleObject? GetChild(int index)
        {
            EnsureChildren();
            return index >= 0 && index < _children.Count ? _children[index] : null;
        }

        private void EnsureChildren()
        {
            if (_children.Count > 0)
            {
                return;
            }

            IReadOnlyList<AlertAction> actions = _owner.Model.Actions ?? [];
            for (int i = 0; i < actions.Count; i++)
            {
                _children.Add(new PopupButtonAccessibleObject(_owner, this, new PopupHit(PopupPart.Action, i), actions[i].Label));
            }

            _children.Add(new PopupButtonAccessibleObject(_owner, this, new PopupHit(PopupPart.Close), "Close"));
        }
    }

    private sealed class PopupButtonAccessibleObject(AlertPopupForm owner, AccessibleObject parent, PopupHit hit, string name) : AccessibleObject
    {
        public override string? Name => name;

        public override AccessibleRole Role => AccessibleRole.PushButton;

        public override AccessibleObject? Parent => parent;

        public override string? DefaultAction => "Press";

        public override AccessibleStates State => owner.Visible ? AccessibleStates.None : AccessibleStates.Invisible;

        public override Rectangle Bounds
        {
            get
            {
                AlertPopupLayout? layout = owner._layout;
                if (layout is null || owner.IsDisposed)
                {
                    return Rectangle.Empty;
                }

                Rectangle local = hit.Part == PopupPart.Close ? layout.CloseBounds : layout.ButtonBounds[hit.Index];
                return owner.RectangleToScreen(local);
            }
        }

        public override void DoDefaultAction()
        {
            if (!owner.IsDisposed)
            {
                owner.Press(hit);
            }
        }
    }
}
