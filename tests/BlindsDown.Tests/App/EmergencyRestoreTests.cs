using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Helper;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.App;

/// <summary>
/// "--restore" with fake microphones, real store files and a fake helper. Runs the same steps as the real one: the
/// engine starts while the helper still has the hardware off, the helper turns it back on, then the microphones.
/// </summary>
public sealed class EmergencyRestoreTests : IDisposable
{
    /// <summary>The hardware behind the microphone the helper had turned off.</summary>
    private const string Hardware = @"USB\VID_1234&PID_0042&MI_00\9&2A2B2C2D&0&0000";

    private readonly TempDirectory _folder = new();
    private readonly LockWorld _world = new();
    private readonly List<string> _finished = [];

    public EmergencyRestoreTests() =>
        _world.UseStores(new FileJournalStore(_folder.File("lock-journal.json")), new FilePendingRestoreStore(PendingFile));

    private string PendingFile => _folder.File("pending-restores.json");

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Finishes_a_restore_behind_helper_hardware_when_no_lock_is_left()
    {
        // An earlier unlock owes "usb" its unmute; the helper still has its hardware off.
        _world.Mics.Add("usb", muted: true, hardware: Hardware);
        _world.SeedPendingRestore("usb");
        _world.Mics.Unplug("usb");
        LockEngine engine = StartRestore();
        Assert.True(File.Exists(PendingFile)); // Its start couldn't finish it: the microphone wasn't there.

        _world.Mics.Plug("usb"); // The helper turned the hardware back on.
        string message = RestoreMicrophones(engine);

        Assert.False(_world.Mics["usb"].Muted);
        Assert.False(File.Exists(PendingFile));
        Assert.Contains("Restored now:", message, StringComparison.Ordinal);
        Assert.Contains("\"Microphone (usb)\" is back and was restored (unmuted, as before the lock).", message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to restore", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start BlindsDown once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finishes_an_earlier_restore_behind_helper_hardware_after_an_unlock_that_owes_nothing()
    {
        _world.Mics.Add("builtin");
        _world.Mics.Add("usb", muted: true, hardware: Hardware);
        _world.SeedPendingRestore("usb");
        _world.Mics.Unplug("usb");
        _world.Engine.Initialize();
        _world.Engine.Lock(); // Locks "builtin" only: "usb" isn't there.
        Assert.True(_world.Mics["builtin"].Muted);

        LockEngine engine = StartRestore();
        _world.Mics.Plug("usb");
        string message = RestoreMicrophones(engine);

        Assert.False(_world.Mics["builtin"].Muted);
        Assert.False(_world.Mics["usb"].Muted);
        Assert.False(File.Exists(PendingFile));
        Assert.StartsWith("Microphones: 1 microphone restored exactly as before.", message, StringComparison.Ordinal);
        Assert.Contains("Since then:", message, StringComparison.Ordinal);
        Assert.Contains("\"Microphone (usb)\" is back and was restored", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_what_is_still_owed_when_no_lock_is_left()
    {
        string message = CommandLineTools.Describe(report: null, helperHadSomething: true, finished: [], stillOwed: true);

        Assert.StartsWith("Microphones: they weren't locked, but an earlier lock still owed some of them a restore.", message, StringComparison.Ordinal);
        Assert.EndsWith("Start BlindsDown once to finish restoring a microphone that wasn't back yet.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to restore", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lists_what_was_finished_when_no_lock_is_left()
    {
        const string Note = "\"Microphone (usb)\" is back and was restored (unmuted, as before the lock).";
        string message = CommandLineTools.Describe(report: null, helperHadSomething: false, finished: [Note], stillOwed: false);

        Assert.Equal(
            "BlindsDown has no active lock, but an earlier lock still owed some microphones a restore.\n\nRestored now:\n• " + Note + "\n",
            message.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(true, "Microphones: nothing to restore (they weren't locked).")]
    [InlineData(false, "Nothing to restore: BlindsDown has no active lock.")]
    public void Says_nothing_to_restore_only_when_nothing_was_owed(bool helperHadSomething, string expected) =>
        Assert.Equal(expected, CommandLineTools.Describe(report: null, helperHadSomething, finished: [], stillOwed: false));

    [Fact]
    public void Asks_the_helper_when_windows_cant_say_whether_it_is_installed()
    {
        var report = new RestoreReport([new RestoreLine(RestoreOutcome.Exact, "Webcam", "Turned back on, as before the lock.")], 1, CoversCameras: true);
        HelperRequest? sent = null;

        string? message = CommandLineTools.UnlockHelper(
            _folder.File("helper-state.txt"),
            QueryFailed,
            request =>
            {
                sent = request;
                return new HelperResponse { Ok = true, Report = report };
            },
            out bool unlocked);

        Assert.Equal(HelperCommand.Unlock, sent?.Command);
        Assert.True(unlocked);
        Assert.StartsWith("Cameras: 1 device restored exactly as before.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_the_cameras_werent_checked_when_neither_windows_nor_the_helper_answers()
    {
        string? message = CommandLineTools.UnlockHelper(
            _folder.File("helper-state.txt"),
            QueryFailed,
            _ => throw new HelperUnavailableException("The camera helper isn't running."),
            out bool unlocked);

        Assert.False(unlocked);
        Assert.NotNull(message);
        Assert.StartsWith("BlindsDown couldn't check your cameras", message, StringComparison.Ordinal);
        Assert.Contains("Run --restore again in a moment", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_nothing_about_cameras_when_windows_says_there_is_no_helper()
    {
        bool asked = false;
        string? message = CommandLineTools.UnlockHelper(
            _folder.File("helper-state.txt"),
            () => new HelperInstallInfo(Installed: false, Running: false, InstalledVersion: null),
            _ =>
            {
                asked = true;
                throw new HelperUnavailableException("The camera helper isn't running.");
            },
            out bool unlocked);

        Assert.Null(message);
        Assert.False(unlocked);
        Assert.False(asked);
    }

    private static HelperInstallInfo QueryFailed() => new(Installed: false, Running: false, InstalledVersion: null, QueryFailed: true);

    /// <summary>Step 1 of --restore: a new engine (a new process) over the stores, started while the helper's hardware is off.</summary>
    private LockEngine StartRestore()
    {
        LockEngine engine = _world.CreateEngine();
        CommandLineTools.CollectFinishedRestores(engine, _finished);
        engine.Initialize();
        engine.NoteDefaultsSettled();
        return engine;
    }

    /// <summary>Step 3 of --restore, after the helper turned <see cref="Hardware"/> back on.</summary>
    private string RestoreMicrophones(LockEngine engine) =>
        CommandLineTools.RestoreMicrophones(
            engine,
            _world.Mics,
            new HashSet<string>([Hardware], StringComparer.OrdinalIgnoreCase),
            () => File.Exists(PendingFile),
            helperHadSomething: true,
            _finished);
}
