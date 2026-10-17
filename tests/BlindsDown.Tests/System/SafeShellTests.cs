using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.SystemServices;

/// <summary>Only the allowlist check is exercised: nothing is ever launched from tests.</summary>
public sealed class SafeShellTests
{
    [Theory]
    [InlineData("ms-settings:privacy-microphone")]
    [InlineData("ms-settings:privacy-webcam")]
    [InlineData("ms-settings:sound")]
    [InlineData("ms-settings:taskbar")]
    public void AllowlistedSettingsPagesAreAllowed(string uri) => Assert.True(SafeShell.IsAllowed(uri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ms-settings:")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-settings:privacy-location")]
    [InlineData("MS-SETTINGS:privacy-microphone")]
    [InlineData("ms-settings:privacy-microphone ")]
    [InlineData(" ms-settings:privacy-microphone")]
    [InlineData("ms-settings:privacy-microphone\0")]
    [InlineData("ms-settings:privacy-microphone&calc")]
    [InlineData("ms-settings:sound?x=1")]
    [InlineData("https://blindsdown-test.invalid/")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\blindsdown-test.invalid\share\setup.exe")]
    [InlineData("notepad.exe")]
    [InlineData("calc")]
    public void EverythingElseIsRejected(string? uri) => Assert.False(SafeShell.IsAllowed(uri));

    [Fact]
    public void PrivacyPagesMatchTheDevice()
    {
        Assert.Equal("ms-settings:privacy-microphone", SafeShell.PrivacySettingsUri(CaptureKind.Microphone));
        Assert.Equal("ms-settings:privacy-webcam", SafeShell.PrivacySettingsUri(CaptureKind.Camera));
        Assert.True(SafeShell.IsAllowed(SafeShell.PrivacySettingsUri(CaptureKind.Microphone)));
        Assert.True(SafeShell.IsAllowed(SafeShell.PrivacySettingsUri(CaptureKind.Camera)));
        Assert.True(SafeShell.IsAllowed(SafeShell.SoundUri));
        Assert.True(SafeShell.IsAllowed(SafeShell.TaskbarUri));
    }
}
