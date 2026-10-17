using BlindsDown.Core.Settings;

namespace BlindsDown.Tests.Core.Settings;

public sealed class HotkeyTests
{
    [Fact]
    public void DefaultIsCtrlAltShiftM()
    {
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'M'), Hotkey.Default);
        Assert.Equal("Ctrl+Alt+Shift+M", Hotkey.Default.ToString());
        Assert.Equal(HotkeyProblem.None, Hotkey.Default.Validate());
    }

    [Theory]
    [InlineData("Ctrl+Alt+Shift+M")]
    [InlineData("Ctrl+F13")]
    [InlineData("F24")]
    [InlineData("Pause")]
    [InlineData("Alt+Win+K")]
    [InlineData("Ctrl+Shift+Win+0")]
    [InlineData("Alt+Num7")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+PageUp")]
    [InlineData("Shift+ScrollLock")]
    [InlineData("Ctrl+NumMultiply")]
    [InlineData("Ctrl+Alt+0xBA")]
    [InlineData("Ctrl+Delete")]
    public void CanonicalTextRoundTrips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(text, hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out Hotkey again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData("ctrl+alt+shift+m", "Ctrl+Alt+Shift+M")]
    [InlineData("  Shift + Control + Alt + M  ", "Ctrl+Alt+Shift+M")]
    [InlineData("WINDOWS+f13", "Win+F13")]
    [InlineData("Ctrl+Ctrl+M", "Ctrl+M")]
    [InlineData("Ctrl+0x41", "Ctrl+A")]
    [InlineData("Ctrl++M", "Ctrl+M")]
    [InlineData("alt+num0", "Alt+Num0")]
    public void ParsingIsForgiving(string text, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(canonical, hotkey.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+M+K")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+0x")]
    [InlineData("Ctrl+0x00")]
    [InlineData("Ctrl+0xFF")]
    [InlineData("Ctrl+0xZZ")]
    [InlineData("Hyper+M")]
    public void InvalidTextIsRejected(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(default, hotkey);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0, HotkeyProblem.NoKey)]
    [InlineData(HotkeyModifiers.Control, 0x10, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Control, 0x11, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Alt, 0x12, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Alt, 0x5B, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Alt, 0x5C, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Alt, 0xA0, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Alt, 0xA5, HotkeyProblem.ModifierOnly)]
    [InlineData(HotkeyModifiers.Control, 0x7B, HotkeyProblem.ReservedF12)]
    [InlineData(HotkeyModifiers.None, 0x7B, HotkeyProblem.ReservedF12)]
    [InlineData(HotkeyModifiers.None, 'M', HotkeyProblem.NeedsModifier)]
    [InlineData(HotkeyModifiers.None, 0x70, HotkeyProblem.NeedsModifier)]
    [InlineData(HotkeyModifiers.None, 0x7C, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.None, 0x87, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.None, 0x13, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Shift, 'M', HotkeyProblem.ShiftWithTypingKey)]
    [InlineData(HotkeyModifiers.Shift, '1', HotkeyProblem.ShiftWithTypingKey)]
    [InlineData(HotkeyModifiers.Shift, 0x74, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Shift | HotkeyModifiers.Control, 'M', HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Win, 'M', HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Control, 0x70, HotkeyProblem.None)]
    public void Validation(HotkeyModifiers modifiers, int key, HotkeyProblem expected) =>
        Assert.Equal(expected, new Hotkey(modifiers, key).Validate());

    [Theory]
    [InlineData('A', "A")]
    [InlineData('9', "9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x60, "Num0")]
    [InlineData(0x13, "Pause")]
    [InlineData(0xBA, "0xBA")]
    [InlineData(0x05, "0x05")]
    public void KeyNames(int key, string name) => Assert.Equal(name, Hotkey.KeyName(key));

    [Fact]
    public void EveryNamedKeyRoundTrips()
    {
        for (int vk = 1; vk < 0xFF; vk++)
        {
            var hotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, vk);
            Assert.True(Hotkey.TryParse(hotkey.ToString(), out Hotkey parsed), hotkey.ToString());
            Assert.Equal(hotkey, parsed);
        }
    }
}
