using System.ComponentModel;
using BlindsDown.Core.Settings;
using BlindsDown.Ui.Forms;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui;

/// <summary>
/// A read-only text box that captures a key combination when focused: shows e.g.
/// "Ctrl+Alt+Shift+M", detects the Win key (GetKeyState), Esc cancels, Backspace clears, rejects F12
/// and bare keys (except F13-F24/Pause) using <see cref="Hotkey.Validate"/>.
///
/// Keys are taken in <see cref="ProcessCmdKey"/>, which runs before WinForms' dialog handling, so Tab,
/// Enter and Alt combinations reach the box instead of moving focus, pressing the default button or
/// opening a menu. Bare Tab / Shift+Tab still move focus and bare Enter still presses OK, so keyboard
/// users are never trapped (none of them would be a usable global hotkey anyway).
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey _value = Hotkey.Default;
    private Hotkey _valueBeforeCapture = Hotkey.Default;
    private bool _capturing;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;

        // Read-only boxes are grey by default, which reads as "disabled"; this one is an input.
        BackColor = SystemColors.Window;
        AccessibleName = "Hotkey";
        AccessibleDescription = "Focus the box and press a key combination. Escape cancels, Backspace clears.";
        ShowValue();
    }

    /// <summary>Raised when the user finished entering a (valid-looking) combination.</summary>
    public event Action<Hotkey>? HotkeyChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            if (!_capturing)
            {
                ShowValue();
            }
        }
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        BeginCapture();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        _capturing = false;
        ShowValue();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_capturing && Focused)
        {
            BeginCapture();
        }
    }

    protected override bool IsInputKey(Keys keyData) => WantsKey(keyData) || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!WantsKey(keyData))
        {
            if (_capturing && (keyData & Keys.KeyCode) is Keys.Return or Keys.Tab)
            {
                // Leaving the box (Tab) or accepting the dialog (Enter): keep the current value.
                _capturing = false;
                ShowValue();
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        HandleKey(keyData);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Normally ProcessCmdKey has already taken the key; this covers hosts without a WinForms message loop.
        if (WantsKey(e.KeyData))
        {
            HandleKey(e.KeyData);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_capturing && IsModifierKey(e.KeyCode))
        {
            // A modifier was released before a key was pressed: update the "Ctrl+Alt+…" preview.
            ShowPreview(ToModifiers(ModifierKeys));
        }
    }

    protected override void Dispose(bool disposing)
    {
        HotkeyChanged = null;
        base.Dispose(disposing);
    }

    private bool WantsKey(Keys keyData)
    {
        if (!Focused)
        {
            return false;
        }

        Keys code = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        if (modifiers == Keys.None && code is Keys.Tab or Keys.Return)
        {
            return false;
        }

        if (modifiers == Keys.Shift && code == Keys.Tab)
        {
            return false;
        }

        // Esc cancels a capture; when nothing is being captured it closes the dialog as usual.
        return modifiers != Keys.None || code != Keys.Escape || _capturing;
    }

    private void HandleKey(Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        HotkeyModifiers modifiers = ToModifiers(keyData & Keys.Modifiers);

        if (modifiers == HotkeyModifiers.None && code == Keys.Escape)
        {
            // Cancel: back to the value from before this capture (even if Backspace cleared it meanwhile).
            _value = _valueBeforeCapture;
            _capturing = false;
            ShowValue();
            return;
        }

        if (!_capturing)
        {
            BeginCapture();
        }

        if (modifiers == HotkeyModifiers.None && code == Keys.Back)
        {
            _value = default;
            SetText(HotkeyText.Capturing);
            return;
        }

        if (IsModifierKey(code))
        {
            ShowPreview(modifiers);
            return;
        }

        var hotkey = new Hotkey(modifiers, (int)code);
        string? problem = HotkeyText.CaptureProblem(hotkey);
        if (problem is not null)
        {
            SetText(problem);
            return;
        }

        _value = hotkey;
        _capturing = false;
        ShowValue();
        HotkeyChanged?.Invoke(hotkey);
    }

    private void BeginCapture()
    {
        _valueBeforeCapture = _value;
        _capturing = true;
        SetText(HotkeyText.Capturing);
    }

    private void ShowValue() => SetText(_value.VirtualKey == 0 ? "None" : _value.ToString());

    private void ShowPreview(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        SetText(parts.Count == 0 ? HotkeyText.Capturing : string.Join("+", parts) + "+…");
    }

    private void SetText(string text)
    {
        Text = text;
        SelectionStart = text.Length;
        SelectionLength = 0;
    }

    private static HotkeyModifiers ToModifiers(Keys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if (modifiers.HasFlag(Keys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(Keys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(Keys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        // Keys has no Win modifier flag.
        if (PopupNative.IsWinKeyDown())
        {
            result |= HotkeyModifiers.Win;
        }

        return result;
    }

    private static bool IsModifierKey(Keys code) =>
        code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu;
}
