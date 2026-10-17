using System.Globalization;
using System.Text;

namespace BlindsDown.Core.Settings;

/// <summary>Hotkey modifier keys. The values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public enum HotkeyProblem
{
    None,
    NoKey,
    ReservedF12,
    NeedsModifier,
    ModifierOnly,

    /// <summary>Shift alone plus a key that types (Shift+A, Shift+1) would steal capitals and symbols in every app.</summary>
    ShiftWithTypingKey,
}

/// <summary>A global hotkey: modifier keys plus one Windows virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int VkPause = 0x13;
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;
    private const int VkF13 = 0x7C;
    private const int VkF24 = 0x87;

    /// <summary>Ctrl+Alt+Shift+M: no Win key (reserved by Windows) and no AltGr clash on European layouts.</summary>
    public static Hotkey Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'M');

    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> KeysByName =
        Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks whether this hotkey is usable. Keys that are pressed without modifiers are only
    /// allowed when no normal typing uses them (F13-F24, Pause).</summary>
    public HotkeyProblem Validate()
    {
        if (VirtualKey == 0)
        {
            return HotkeyProblem.NoKey;
        }

        if (IsModifierKey(VirtualKey))
        {
            return HotkeyProblem.ModifierOnly;
        }

        if (VirtualKey == VkF12)
        {
            return HotkeyProblem.ReservedF12; // Windows reserves F12 for the debugger.
        }

        bool standalone = VirtualKey is >= VkF13 and <= VkF24 or VkPause;
        if (Modifiers == HotkeyModifiers.None && !standalone)
        {
            return HotkeyProblem.NeedsModifier;
        }

        if (Modifiers == HotkeyModifiers.Shift && IsTypingKey(VirtualKey))
        {
            return HotkeyProblem.ShiftWithTypingKey;
        }

        return HotkeyProblem.None;
    }

    /// <summary>Keys that produce or edit text: letters, digits, the number pad, Space, Enter, Tab,
    /// Backspace and the punctuation (OEM) keys.</summary>
    public static bool IsTypingKey(int virtualKey) =>
        virtualKey is (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or (>= 0x60 and <= 0x6F) // VK_NUMPAD0 .. VK_DIVIDE
            or 0x20 or 0x0D or 0x09 or 0x08 // Space, Enter, Tab, Backspace
            or (>= 0xBA and <= 0xE2); // VK_OEM_1 .. VK_OEM_102

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            sb.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            sb.Append("Win+");
        }

        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    public static string KeyName(int virtualKey) =>
        Names.TryGetValue(virtualKey, out string? name)
            ? name
            : "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>Parses text like "Ctrl+Alt+Shift+M" (case-insensitive).</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (string rawPart in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (key is not null)
            {
                return false; // Two non-modifier keys.
            }

            if (KeysByName.TryGetValue(rawPart, out int vk))
            {
                key = vk;
            }
            else if (part.StartsWith("0X", StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
                && hex is > 0 and < 0xFF)
            {
                key = hex;
            }
            else
            {
                return false;
            }
        }

        if (key is null)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    private static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 // Shift, Ctrl, Alt
            or 0x5B or 0x5C // Left/right Win
            or >= 0xA0 and <= 0xA5; // Left/right Shift, Ctrl, Alt

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        for (int c = 'A'; c <= 'Z'; c++)
        {
            names[c] = ((char)c).ToString();
        }

        for (int d = '0'; d <= '9'; d++)
        {
            names[d] = ((char)d).ToString();
        }

        for (int f = 0; f < 24; f++)
        {
            names[VkF1 + f] = "F" + (f + 1).ToString(CultureInfo.InvariantCulture);
        }

        for (int n = 0; n <= 9; n++)
        {
            names[0x60 + n] = "Num" + n.ToString(CultureInfo.InvariantCulture);
        }

        names[VkPause] = "Pause";
        names[0x20] = "Space";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x23] = "End";
        names[0x24] = "Home";
        names[0x25] = "Left";
        names[0x26] = "Up";
        names[0x27] = "Right";
        names[0x28] = "Down";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x91] = "ScrollLock";
        names[0x6A] = "NumMultiply";
        names[0x6B] = "NumAdd";
        names[0x6D] = "NumSubtract";
        names[0x6F] = "NumDivide";
        return names;
    }
}
