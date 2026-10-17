using BlindsDown.Core.Settings;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui;

/// <summary>
/// Registers the global hotkey (RegisterHotKey with MOD_NOREPEAT on a message-only window
/// owned by the UI thread). <see cref="Pressed"/> is raised on the UI thread.
///
/// Why a message-only window: it never appears anywhere, receives no broadcasts, and WM_HOTKEY is
/// posted to the thread that owns it, i.e. the UI thread. MOD_NOREPEAT stops a held key from toggling
/// the lock on and off repeatedly. A replacement hotkey is registered BEFORE the old one is released,
/// so a failed change never leaves the user without a working hotkey.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int FirstId = 1;
    private const int SecondId = 2;

    private readonly MessageWindow _window;
    private int _registeredId;
    private bool _suspended;
    private bool _disposed;

    /// <summary>Create on the UI thread (the thread that will receive <see cref="Pressed"/>).</summary>
    public HotkeyManager()
    {
        _window = new MessageWindow(this);
    }

    public event Action? Pressed;

    public Hotkey? Current { get; private set; }

    /// <summary>True while <see cref="Current"/> is registered with Windows (false while suspended or after a
    /// failed <see cref="Resume"/>, e.g. because another app took the combination in the meantime).</summary>
    public bool IsRegistered => _registeredId != 0;

    /// <summary>The message-only window that receives WM_HOTKEY (tests post to it).</summary>
    internal nint WindowHandle => _window.Handle;

    /// <summary>The RegisterHotKey id that is live right now (0 = none); WM_HOTKEY for any other id is ignored.</summary>
    internal int RegisteredId => _registeredId;

    /// <summary>Replaces the current registration. On failure the previous hotkey stays registered and
    /// <paramref name="error"/> explains why (e.g. "Ctrl+Alt+Shift+M is already used by another app").
    /// While suspended, the new hotkey is only test-registered (to prove it is free) and released again;
    /// <see cref="Resume"/> then registers it.</summary>
    public bool TryRegister(Hotkey hotkey, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "The hotkey can't be changed while BlindsDown is closing.";
            return false;
        }

        HotkeyProblem problem = hotkey.Validate();
        if (problem != HotkeyProblem.None)
        {
            error = DescribeProblem(hotkey, problem);
            return false;
        }

        if (Current == hotkey && (IsRegistered || _suspended))
        {
            return true;
        }

        int id = _registeredId == FirstId ? SecondId : FirstId;
        if (!Register(hotkey, id, out int win32Error))
        {
            error = DescribeFailure(hotkey, win32Error);
            Log.Warn($"RegisterHotKey({hotkey}) failed with error {win32Error}.");
            return false;
        }

        if (_suspended)
        {
            Unregister(id);
        }
        else
        {
            if (_registeredId != 0)
            {
                Unregister(_registeredId);
            }

            _registeredId = id;
        }

        Current = hotkey;
        Log.Info($"Hotkey set to {hotkey}{(_suspended ? " (registers on resume)" : string.Empty)}.");
        return true;
    }

    /// <summary>Temporarily releases the hotkey (e.g. while the settings dialog captures a new one).</summary>
    public void Suspend()
    {
        if (_disposed || _suspended)
        {
            return;
        }

        _suspended = true;
        if (_registeredId != 0)
        {
            Unregister(_registeredId);
            _registeredId = 0;
        }
    }

    /// <summary>Re-registers <see cref="Current"/> after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        if (_disposed || !_suspended)
        {
            return;
        }

        _suspended = false;
        if (Current is not Hotkey hotkey)
        {
            return;
        }

        if (Register(hotkey, FirstId, out int win32Error))
        {
            _registeredId = FirstId;
        }
        else
        {
            Log.Warn($"Could not re-register hotkey {hotkey} after the settings dialog: {DescribeFailure(hotkey, win32Error)}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registeredId != 0)
        {
            Unregister(_registeredId);
            _registeredId = 0;
        }

        _window.DestroyHandle();
        Pressed = null;
        Current = null;
    }

    /// <summary>Plain-language reason why a hotkey can't be used at all.</summary>
    internal static string DescribeProblem(Hotkey hotkey, HotkeyProblem problem) => problem switch
    {
        HotkeyProblem.NoKey => "Press a key combination, for example Ctrl+Alt+Shift+M.",
        HotkeyProblem.ModifierOnly => "Add a normal key to the modifier keys, for example Ctrl+Alt+Shift+M.",
        HotkeyProblem.ReservedF12 => "F12 is reserved by Windows for debuggers. Choose another key.",
        HotkeyProblem.NeedsModifier => $"{Hotkey.KeyName(hotkey.VirtualKey)} on its own would fire while you type. Add Ctrl, Alt or Shift (only F13-F24 and Pause work alone).",
        _ => $"{hotkey} can't be used as a hotkey.",
    };

    /// <summary>Plain-language reason why Windows refused to register a hotkey.</summary>
    internal static string DescribeFailure(Hotkey hotkey, int win32Error) => win32Error switch
    {
        TrayNative.ErrorHotkeyAlreadyRegistered =>
            $"{hotkey} is already used by another app (or by Windows). Choose a different combination.",
        TrayNative.ErrorInvalidParameter =>
            $"Windows does not accept {hotkey} as a hotkey. Choose a different combination.",
        _ => $"Windows could not register {hotkey} (error {win32Error}).",
    };

    private bool Register(Hotkey hotkey, int id, out int win32Error)
    {
        uint modifiers = TrayNative.ModNoRepeat | (uint)hotkey.Modifiers;
        if (TrayNative.RegisterHotKey(_window.Handle, id, modifiers, (uint)hotkey.VirtualKey) != 0)
        {
            win32Error = 0;
            return true;
        }

        win32Error = TrayNative.GetLastError();
        return false;
    }

    private void Unregister(int id)
    {
        if (TrayNative.UnregisterHotKey(_window.Handle, id) == 0)
        {
            Log.Warn($"UnregisterHotKey({id}) failed with error {TrayNative.GetLastError()}.");
        }
    }

    private void OnHotkey(int id)
    {
        if (id == 0 || id != _registeredId)
        {
            return;
        }

        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            // An exception escaping WndProc would end up in WinForms' crash dialog.
            Log.Error("Hotkey action failed", ex);
        }
    }

    /// <summary>A message-only window (parent HWND_MESSAGE): invisible, no broadcasts, just WM_HOTKEY.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly HotkeyManager _owner;

        public MessageWindow(HotkeyManager owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "BlindsDown hotkey", Parent = TrayNative.HwndMessage });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TrayNative.WmHotkey)
            {
                _owner.OnHotkey((int)m.WParam);
                return;
            }

            base.WndProc(ref m);
        }
    }
}
