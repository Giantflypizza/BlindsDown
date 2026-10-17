using BlindsDown.Core.Settings;

namespace BlindsDown.Tests;

public class SmokeTests
{
    [Fact]
    public void DefaultHotkeyIsValid() =>
        Assert.Equal(HotkeyProblem.None, Hotkey.Default.Validate());
}
