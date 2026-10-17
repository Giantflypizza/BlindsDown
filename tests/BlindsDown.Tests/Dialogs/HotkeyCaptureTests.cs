using BlindsDown.Core.Settings;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class HotkeyCaptureTests
{
    // HotkeyModifiers values (MOD_*): Alt = 1, Control = 2, Shift = 4, Win = 8.
    [Theory]
    [InlineData(2 | 1 | 4, 'M')] // Ctrl+Alt+Shift+M, the default
    [InlineData(2 | 1, 'K')] // Ctrl+Alt+K
    [InlineData(2, 'A')] // Ctrl+A
    [InlineData(1 | 4, '1')] // Alt+Shift+1
    [InlineData(4, 0x74)] // Shift+F5: does not type anything
    [InlineData(0, 0x7C)] // F13 on its own (macro keys)
    [InlineData(0, 0x13)] // Pause on its own
    [InlineData(4 | 8, 'L')] // Shift+Win+L: Shift is not alone
    public void UsableCombinationsAreAccepted(int modifiers, int virtualKey) =>
        Assert.Null(HotkeyText.CaptureProblem(new Hotkey((HotkeyModifiers)modifiers, virtualKey)));

    [Theory]
    [InlineData('A')]
    [InlineData('7')]
    [InlineData(0x20)] // Space
    [InlineData(0x0D)] // Enter
    [InlineData(0x61)] // NumPad1
    [InlineData(0xBA)] // ; on US layouts (VK_OEM_1)
    [InlineData(0xE2)] // < > on European layouts (VK_OEM_102)
    public void ShiftWithATypingKeyIsRejected(int virtualKey) =>
        Assert.Equal("Add Ctrl or Alt", HotkeyText.CaptureProblem(new Hotkey(HotkeyModifiers.Shift, virtualKey)));

    [Fact]
    public void BareTypingKeysNeedAModifier() =>
        Assert.Equal(HotkeyText.ShortHint(HotkeyProblem.NeedsModifier), HotkeyText.CaptureProblem(new Hotkey(HotkeyModifiers.None, 'M')));

    [Fact]
    public void F12IsReservedEvenWithModifiers() =>
        Assert.Equal(
            HotkeyText.ShortHint(HotkeyProblem.ReservedF12),
            HotkeyText.CaptureProblem(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7B)));
}
