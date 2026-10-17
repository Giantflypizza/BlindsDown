using BlindsDown.Infrastructure;
using Microsoft.Win32;

namespace BlindsDown.Tests.SystemServices;

/// <summary>Runs entirely under a throwaway HKCU\Software\BlindsDownTests\{guid} key standing in for HKCU;
/// the real Run and StartupApproved keys are never touched.</summary>
public sealed class AutostartTests : IDisposable
{
    private const string Exe = @"C:\Program Files\BlindsDown\BlindsDown.exe";
    private readonly TestRegistryKey _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void EnableWritesAQuotedCommandAndDisableRemovesIt()
    {
        Assert.False(Autostart.IsEnabled(_root.Key, Exe));

        Autostart.SetEnabled(_root.Key, true, Exe);

        Assert.True(Autostart.IsEnabled(_root.Key, Exe));
        using (RegistryKey? run = _root.Key.OpenSubKey(Autostart.RunKeyPath))
        {
            Assert.Equal("\"C:\\Program Files\\BlindsDown\\BlindsDown.exe\" --autostart", run?.GetValue("BlindsDown"));
            Assert.Equal(RegistryValueKind.String, run?.GetValueKind("BlindsDown"));
        }

        Autostart.SetEnabled(_root.Key, false, Exe);

        Assert.False(Autostart.IsEnabled(_root.Key, Exe));
        using (RegistryKey? run = _root.Key.OpenSubKey(Autostart.RunKeyPath))
        {
            Assert.Null(run?.GetValue("BlindsDown"));
        }

        // Disabling twice is harmless.
        Autostart.SetEnabled(_root.Key, false, Exe);
    }

    [Fact]
    public void EnablingNeverTouchesTheTaskManagerSwitch()
    {
        Autostart.SetEnabled(_root.Key, true, Exe);
        Autostart.SetEnabled(_root.Key, false, Exe);

        Assert.Null(_root.Key.OpenSubKey(Autostart.StartupApprovedRunKeyPath));
    }

    [Fact]
    public void AnEntryForAnotherCopyCountsAsOff()
    {
        using (RegistryKey run = _root.Key.CreateSubKey(Autostart.RunKeyPath, writable: true))
        {
            run.SetValue("BlindsDown", "\"D:\\Old\\BlindsDown.exe\" --autostart");
        }

        Assert.False(Autostart.IsEnabled(_root.Key, Exe));
        Assert.True(Autostart.IsEnabled(_root.Key, @"d:\old\blindsdown.EXE"));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void TaskManagerStateIsReadFromTheFirstByte(byte[] approved, bool disabled)
    {
        Autostart.SetEnabled(_root.Key, true, Exe);
        using (RegistryKey key = _root.Key.CreateSubKey(Autostart.StartupApprovedRunKeyPath, writable: true))
        {
            key.SetValue("BlindsDown", approved, RegistryValueKind.Binary);
        }

        Assert.Equal(disabled, Autostart.IsDisabledInTaskManager(_root.Key, Exe));
    }

    [Fact]
    public void TaskManagerStateIsIrrelevantWhenAutostartIsOff()
    {
        using (RegistryKey key = _root.Key.CreateSubKey(Autostart.StartupApprovedRunKeyPath, writable: true))
        {
            key.SetValue("BlindsDown", new byte[] { 0x03, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.False(Autostart.IsDisabledInTaskManager(_root.Key, Exe));
    }

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" --autostart", @"C:\A B\x.exe")]
    [InlineData("C:\\Tools\\x.exe --autostart", @"C:\Tools\x.exe")]
    [InlineData("  \"C:\\x.exe\"", @"C:\x.exe")]
    [InlineData("\"C:\\unterminated.exe", @"C:\unterminated.exe")]
    [InlineData("C:\\noext", @"C:\noext")]
    public void ExePathIsParsedFromTheCommandLine(string command, string expected) =>
        Assert.Equal(expected, Autostart.ExePathFromCommand(command));
}
