using System.Runtime.InteropServices;
using BlindsDown.Core.Settings;
using BlindsDown.Ui;

namespace BlindsDown.Tests.Tray;

public partial class HotkeyManagerTests
{
    private const uint WmHotkey = 0x0312;

    /// <summary>Unlikely to be used by anything else on a developer's machine.</summary>
    private static readonly Hotkey TestHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87); // F24

    private static readonly Hotkey OtherTestHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x86); // F23

    [Fact]
    public void AlreadyRegisteredErrorIsReadable()
    {
        string text = HotkeyManager.DescribeFailure(Hotkey.Default, 1409);
        Assert.Equal("Ctrl+Alt+Shift+M is already used by another app (or by Windows). Choose a different combination.", text);
    }

    [Fact]
    public void OtherErrorsMentionTheHotkeyAndCode()
    {
        Assert.Contains("does not accept Ctrl+Alt+Shift+M", HotkeyManager.DescribeFailure(Hotkey.Default, 87), StringComparison.Ordinal);
        string unknown = HotkeyManager.DescribeFailure(Hotkey.Default, 5);
        Assert.Contains("Ctrl+Alt+Shift+M", unknown, StringComparison.Ordinal);
        Assert.Contains("error 5", unknown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HotkeyProblem.NoKey, "Press a key combination")]
    [InlineData(HotkeyProblem.ModifierOnly, "Add a normal key")]
    [InlineData(HotkeyProblem.ReservedF12, "F12 is reserved")]
    [InlineData(HotkeyProblem.NeedsModifier, "M on its own")]
    public void ValidationProblemsAreExplained(HotkeyProblem problem, string expectedStart)
    {
        var hotkey = new Hotkey(HotkeyModifiers.None, 'M');
        Assert.StartsWith(expectedStart, HotkeyManager.DescribeProblem(hotkey, problem), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidHotkeyIsRejectedWithoutRegistering()
    {
        // Validation runs before RegisterHotKey; only an invisible message-only window is created.
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var manager = new HotkeyManager();
            Assert.False(manager.TryRegister(new Hotkey(HotkeyModifiers.Control, 0x7B), out string? error)); // Ctrl+F12
            Assert.StartsWith("F12 is reserved", error, StringComparison.Ordinal);
            Assert.Null(manager.Current);
            Assert.False(manager.IsRegistered);
        });
    }

    [Theory]
    [InlineData(false)] // Settings closed with Cancel after trying another hotkey.
    [InlineData(true)] // Settings closed with OK, new hotkey saved.
    public void SettingsDialogSequenceLeavesTheHotkeyRegistered(bool saveNewHotkey)
    {
        // The same calls AppHost makes around the Settings dialog. Regression test: Resume() was once
        // never called, so the hotkey silently stopped working after Settings was opened.
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var manager = new HotkeyManager();
            Assert.True(manager.TryRegister(TestHotkey, out _));

            manager.Suspend(); // Settings opens.
            Assert.True(manager.TryRegister(OtherTestHotkey, out _)); // The user tries another combination.

            Hotkey saved = saveNewHotkey ? OtherTestHotkey : TestHotkey;
            manager.Resume(); // Settings closes...
            Assert.True(manager.TryRegister(saved, out string? error), error); // ...and the saved one is registered.

            Assert.True(manager.IsRegistered);
            Assert.Equal(saved, manager.Current);
        });
    }

    [Fact]
    public void RegistersReplacesSuspendsAndResumesForReal()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var first = new HotkeyManager();
            using var second = new HotkeyManager();

            Assert.True(first.TryRegister(TestHotkey, out string? error), error);
            Assert.True(first.IsRegistered);

            // The same combination is now taken: the second manager gets the readable error.
            Assert.False(second.TryRegister(TestHotkey, out error));
            Assert.Contains("already used by another app", error, StringComparison.Ordinal);
            Assert.Null(second.Current);

            // Replacing keeps working and frees the old combination.
            Assert.True(first.TryRegister(OtherTestHotkey, out error), error);
            Assert.Equal(OtherTestHotkey, first.Current);
            Assert.True(second.TryRegister(TestHotkey, out error), error);

            // While suspended the hotkey is released (another app could take it) and comes back on resume.
            first.Suspend();
            Assert.False(first.IsRegistered);
            using (var probe = new HotkeyManager())
            {
                Assert.True(probe.TryRegister(OtherTestHotkey, out error), error);
            }

            first.Resume();
            Assert.True(first.IsRegistered);
        });
    }

    [Fact]
    public void FailedReplacementKeepsThePreviousHotkeyRegistered()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var otherApp = new HotkeyManager();
            using var manager = new HotkeyManager();
            Assert.True(otherApp.TryRegister(OtherTestHotkey, out string? error), error);
            Assert.True(manager.TryRegister(TestHotkey, out error), error);

            Assert.False(manager.TryRegister(OtherTestHotkey, out error));
            Assert.Contains("already used by another app", error, StringComparison.Ordinal);

            // The contract: the user is never left without a working hotkey.
            Assert.Equal(TestHotkey, manager.Current);
            Assert.True(manager.IsRegistered);
            Assert.False(IsFree(TestHotkey), "the previous hotkey must still be held");
        });
    }

    [Fact]
    public void ChangingWhileSuspendedOnlyChecksAvailabilityUntilResume()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var manager = new HotkeyManager();
            Assert.True(manager.TryRegister(TestHotkey, out string? error), error);

            manager.Suspend();
            Assert.True(IsFree(TestHotkey), "suspend releases the hotkey so the settings box can capture it");

            Assert.True(manager.TryRegister(OtherTestHotkey, out error), error);
            Assert.Equal(OtherTestHotkey, manager.Current);
            Assert.False(manager.IsRegistered);
            Assert.True(IsFree(OtherTestHotkey), "while suspended the new hotkey is only test-registered");

            manager.Resume();
            Assert.True(manager.IsRegistered);
            Assert.False(IsFree(OtherTestHotkey), "resume registers the new hotkey");
            Assert.True(IsFree(TestHotkey), "the old hotkey stays released");
        });
    }

    [Fact]
    public void WmHotkeyForTheLiveRegistrationRaisesPressedOnTheUiThread()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var manager = new HotkeyManager();
            int uiThread = Environment.CurrentManagedThreadId;
            var pressedOn = new List<int>();
            manager.Pressed += () => pressedOn.Add(Environment.CurrentManagedThreadId);
            Assert.True(manager.TryRegister(TestHotkey, out string? error), error);

            int live = manager.RegisteredId;
            Assert.NotEqual(0, PostMessageW(manager.WindowHandle, WmHotkey, live, 0));
            Assert.NotEqual(0, PostMessageW(manager.WindowHandle, WmHotkey, live + 1, 0)); // stale or foreign id
            PumpMessages();
            Assert.Equal([uiThread], pressedOn);

            // A press that was queued just before the settings dialog suspended the hotkey does nothing.
            Assert.NotEqual(0, PostMessageW(manager.WindowHandle, WmHotkey, live, 0));
            manager.Suspend();
            PumpMessages();
            Assert.Single(pressedOn);

            // Handler failures are logged, never thrown into the message loop.
            manager.Resume();
            manager.Pressed += () => throw new InvalidOperationException("handler failed");
            Assert.NotEqual(0, PostMessageW(manager.WindowHandle, WmHotkey, manager.RegisteredId, 0));
            PumpMessages();
            Assert.Equal(2, pressedOn.Count);
        });
    }

    /// <summary>True when nothing (in this or another process) holds <paramref name="hotkey"/> right now.</summary>
    private static bool IsFree(Hotkey hotkey)
    {
        using var probe = new HotkeyManager();
        return probe.TryRegister(hotkey, out _);
    }

    private static void PumpMessages()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
