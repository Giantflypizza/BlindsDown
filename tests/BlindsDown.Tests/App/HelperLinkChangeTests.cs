using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Helper;
using BlindsDown.Tests.Core.Pnp;

namespace BlindsDown.Tests.App;

/// <summary>
/// The tray app tells its own changes to the helper's lock from another program's by the request IDs it sends: an
/// unlock by another program must never be quiet, not even when it happens between two polls, while this app is busy
/// with its own request, or while BlindsDown isn't running; and another program must not be able to pass its change
/// off as the app's. End to end over a test pipe (never the real helper's).
/// </summary>
public sealed class HelperLinkChangeTests : IDisposable
{
    private readonly string _pipe = "BlindsDownTests-" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "BlindsDownTests-" + Guid.NewGuid().ToString("N"));
    private readonly PnpWorld _world = new();
    private readonly HelperRequestHandler _handler;
    private readonly HelperPipeServer _server;
    private readonly Lock _gate = new();

    public HelperLinkChangeTests()
    {
        Directory.CreateDirectory(_folder);
        _world.Devices.Add("cam1");
        _world.Engine.Initialize();
        _handler = new HelperRequestHandler(_world.Engine, AppVersion.Text);
        _server = new HelperPipeServer(_pipe, request =>
        {
            lock (_gate)
            {
                return _handler.HandleMessage(request);
            }
        }, forTests: true);
        _server.Start();
    }

    private string StateFile => Path.Combine(_folder, "helper-state.txt");

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task OwnChangesAreOwnAndAnotherProgramsUnlockIsNot()
    {
        using HelperLink link = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(link);
        await link.RefreshAsync();

        (DeviceLockStatus? status, string? error) = await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        Assert.Null(error);
        Assert.NotNull(status);
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "someone-else" });
        await link.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Locked, true), (HelperChangeKind.Unlocked, false)], changes);
    }

    [Fact]
    public async Task AnUnlockAndLockBetweenTwoPollsIsStillSeenAsOneAnswer()
    {
        using HelperLink link = NewLink();
        var batches = new List<HelperChangeBatch>();
        link.ChangesReceived += batches.Add;
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        batches.Clear();

        // Unlock, use the camera, lock again: the status afterwards looks exactly like before.
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "x1" });
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, RequestId = "x2" });
        await link.RefreshAsync();

        HelperChangeBatch batch = Assert.Single(batches); // One alarm for the whole answer, not one per change.
        Assert.True(batch.HeldBefore);
        Assert.Equal([(HelperChangeKind.Unlocked, false), (HelperChangeKind.Locked, false)], batch.Changes.Select(c => (c.Change.Kind, c.Own)));
        Assert.False(link.HeldOwnLock); // The lock in force now is another program's.
    }

    [Fact]
    public async Task AChangeMadeWhileBlindsDownWasClosedIsReportedAtTheNextStartWithItsTime()
    {
        using (HelperLink first = NewLink())
        {
            await first.RefreshAsync();
            await first.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        }

        // BlindsDown isn't running: another program unlocks and locks again, and the helper raises an alert meanwhile.
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "while-closed" });
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, RequestId = "while-closed-2" });
        lock (_gate)
        {
            _world.Devices.SomeoneEnables("cam1");
            _world.Pump();
            Assert.NotEmpty(_world.AlertsOf(EngineAlertKind.ReEnabledWhileLocked)); // The alert really exists.
        }

        using HelperLink second = NewLink();
        Assert.True(second.HeldOwnLock); // Remembered from the first run.
        var batches = new List<HelperChangeBatch>();
        var alerts = 0;
        second.ChangesReceived += batches.Add;
        second.EventReceived += _ => alerts++;
        await second.RefreshAsync();

        HelperChangeBatch batch = Assert.Single(batches);
        Assert.True(batch.HeldBefore);
        Assert.Equal([HelperChangeKind.Unlocked, HelperChangeKind.Locked], batch.Changes.Where(c => !c.Own).Select(c => c.Change.Kind));
        Assert.All(batch.Changes, c => Assert.InRange(c.Change.At, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Equal(0, alerts); // Old alerts are not replayed at a start.
    }

    [Fact]
    public async Task ARestoreRunInItsOwnProcessIsNotAnotherProgram()
    {
        using HelperLink link = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(link);
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");

        // --restore notes its ID in the shared file before it sends the unlock.
        string id = HelperLink.NewRequestId();
        HelperLink.NoteOwnRequest(StateFile, id);
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = id, Source = "--restore" });

        // The running app reads its own IDs at start; a fresh link (the next start) knows the --restore ID.
        using HelperLink next = NewLink();
        List<(HelperChangeKind Kind, bool Own)> later = Record(next);
        await next.RefreshAsync();

        Assert.DoesNotContain(later.Concat(changes), c => !c.Own);
        Assert.False(next.HeldOwnLock);
    }

    [Fact]
    public async Task WhatTheHelperPublishesCantBeUsedToPassAnUnlockOffAsTheAppsOwn()
    {
        using HelperLink link = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(link);
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");

        // Another program reads the change log (any client can) and sends what it found as its own request ID.
        HelperResponse status = await SendAsync(new HelperRequest { Command = HelperCommand.Status });
        string published = Assert.Single(status.Changes).RequestDigest!;
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = published });
        await link.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Locked, true), (HelperChangeKind.Unlocked, false)], changes);
    }

    [Fact]
    public async Task AnUnlocksIdNeverCoversALockChange()
    {
        // An ID this app noted for an unlock (say, of a --restore that never ran) used for a lock is not this app's.
        string id = HelperLink.NewRequestId();
        HelperLink.NoteOwnRequest(StateFile, id);
        using HelperLink restarted = NewLink();
        List<(HelperChangeKind Kind, bool Own)> later = Record(restarted);
        await restarted.RefreshAsync();
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, RequestId = id });
        await restarted.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Locked, false)], later);
    }

    [Fact]
    public async Task TheStateFileKeepsOnlyFingerprintsAndWhetherTheLockIsHeld()
    {
        using HelperLink link = NewLink();
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        HelperResponse status = await SendAsync(new HelperRequest { Command = HelperCommand.Status });
        string digest = Assert.Single(status.Changes).RequestDigest!;

        string[] lines = File.ReadAllLines(StateFile);

        Assert.Contains("held=1", lines);
        Assert.Contains("own=L:" + digest, lines);
        Assert.All(lines.Where(l => l.StartsWith("own=", StringComparison.Ordinal)), l => Assert.Matches("^own=[LU]:[0-9a-f]{64}$", l));
        Assert.True(link.HeldOwnLock);

        await link.UnlockAsync("hotkey");

        Assert.False(link.HeldOwnLock);
        Assert.DoesNotContain("held=1", File.ReadAllLines(StateFile));
    }

    [Fact]
    public void AnEarlierFormatOfTheStateFileKeepsItsPositionButNotItsIds()
    {
        File.WriteAllLines(StateFile, ["cursor=5", "own=0123456789abcdef0123456789abcdef"]);

        HelperLink.SavedState saved = HelperLink.ReadState(StateFile);

        Assert.Equal(5, saved.Cursor);
        Assert.False(saved.Held);
        Assert.Empty(saved.Own);
    }

    [Fact]
    public async Task WhenThePositionIsAheadOfTheHelperItsWholeCurrentRunIsNew()
    {
        using (HelperLink first = NewLink())
        {
            await first.RefreshAsync();
            await first.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        }

        // An earlier run of the helper had a clock far ahead: the saved position is beyond anything this helper has.
        string[] lines = File.ReadAllLines(StateFile);
        File.WriteAllLines(StateFile, lines.Select(l => l.StartsWith("cursor=", StringComparison.Ordinal) ? "cursor=9000000000000000" : l));
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "after-the-clock-went-back" });

        using HelperLink second = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(second);
        await second.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Locked, true), (HelperChangeKind.Unlocked, false)], changes);
        Assert.DoesNotContain("cursor=9000000000000000", File.ReadAllLines(StateFile)); // Back in step with the helper.
    }

    [Fact]
    public async Task APositionFromAnotherRunOfTheHelperGetsThisWholeRun()
    {
        using (HelperLink first = NewLink())
        {
            await first.RefreshAsync();
            await first.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        }

        string[] saved = File.ReadAllLines(StateFile);
        Assert.Contains(saved, l => l.StartsWith("run=", StringComparison.Ordinal)); // Kept with the position.

        // The saved position came from an earlier run of the helper whose IDs reach into this run's.
        File.WriteAllLines(StateFile, saved.Select(l => l.StartsWith("run=", StringComparison.Ordinal) ? "run=1" : l));
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "other-run" });

        using HelperLink second = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(second);
        await second.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Locked, true), (HelperChangeKind.Unlocked, false)], changes);
        Assert.DoesNotContain("run=1", File.ReadAllLines(StateFile));
    }

    [Fact]
    public async Task TheHelpersAlertAboutANarrowingComesWithItsChangeNotOnItsOwn()
    {
        using HelperLink link = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(link);
        var alerts = new List<HelperEvent>();
        link.EventReceived += alerts.Add;
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");

        // Another program sends a lock that leaves the camera out: the engine alerts, and the change says who did it.
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest { MicHardware = true }, RequestId = "narrower" });
        await link.RefreshAsync();

        Assert.Equal((HelperChangeKind.Narrowed, false), changes[^1]);
        Assert.DoesNotContain(alerts, HelperLink.IsNarrowedAlert);
    }

    [Fact]
    public async Task ASetupRunsUnlockIsOwnAndItsIdsAreForgottenAfterwards()
    {
        string? given = null;
        using HelperLink link = NewLink(async (task, _, requestId) =>
        {
            // The elevated removal: it first unlocks with the ID the tray app gave it (here it then fails, so the helper stays).
            given = requestId;
            await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = requestId, Source = "setup" });
            return 1;
        });
        List<(HelperChangeKind Kind, bool Own)> changes = Record(link);
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");

        await link.RunSetupAsync(ElevatedTask.UninstallHelper, owner: null);

        Assert.NotNull(given);
        Assert.Equal([(HelperChangeKind.Locked, true), (HelperChangeKind.Unlocked, true)], changes);
        Assert.DoesNotContain(File.ReadAllLines(StateFile), l => l.StartsWith("own=S:", StringComparison.Ordinal));

        // The run's other IDs were on its command line: a program that read them gets no cover from them now.
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = given + "-2" });
        await link.RefreshAsync();

        Assert.Equal((HelperChangeKind.Unlocked, false), changes[^1]);
    }

    [Fact]
    public async Task YourOwnRemovalEndsTheHeldLockEvenWhileTheServiceEntryLingers()
    {
        using HelperLink link = NewLink(async (_, _, requestId) =>
        {
            await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = requestId, Source = "setup" });
            return 0; // Removed; Windows still lists the service until it has stopped (the test query keeps saying installed).
        });
        await link.RefreshAsync();
        await link.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        Assert.True(link.HeldOwnLock);

        await link.RunSetupAsync(ElevatedTask.UninstallHelper, owner: null);

        Assert.False(link.HeldOwnLock);
        Assert.DoesNotContain("held=1", File.ReadAllLines(StateFile));
    }

    [Fact]
    public async Task AnUpdateNeverUnlocksSoItGetsNoRequestId()
    {
        var given = new List<string?>();
        using HelperLink link = NewLink((_, _, requestId) =>
        {
            given.Add(requestId);
            return Task.FromResult<int?>(0);
        });
        await link.RefreshAsync();

        await link.RunSetupAsync(ElevatedTask.UpdateHelper, owner: null);
        await link.RunSetupAsync(ElevatedTask.InstallHelper, owner: null); // Over an installed helper: an update too.

        Assert.Equal([null, null], given);
        Assert.DoesNotContain(File.ReadAllLines(StateFile), l => l.StartsWith("own=S:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreshInstallGetsARequestIdForTheUnlockOfALeftoverLock()
    {
        bool installed = false;
        string? given = null;
        using var link = new HelperLink(
            StateFile,
            new HelperClient(_pipe, requireSystemOwner: false),
            () => new HelperInstallInfo(installed, installed, installed ? AppVersion.Text : null),
            (_, _, requestId) =>
            {
                given = requestId;
                installed = true;
                return Task.FromResult<int?>(0);
            });
        await link.RefreshAsync();
        Assert.False(link.IsInstalled);

        await link.RunSetupAsync(ElevatedTask.InstallHelper, owner: null);

        Assert.Matches("^[0-9a-f]{32}$", given);
    }

    [Fact]
    public void AStateFileThatCantBeReadIsReportedEvenWhenAnOlderCopyIsUsed()
    {
        HelperLink.WriteState(StateFile, new HelperLink.SavedState(5, false, []));
        HelperLink.WriteState(StateFile, new HelperLink.SavedState(7, true, [])); // Leaves the first save as the backup.
        using (new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            HelperLink.SavedState saved = HelperLink.ReadState(StateFile);

            Assert.True(saved.Unreadable);
            Assert.Equal(5, saved.Cursor); // The backup, one save behind.
        }
    }

    [Fact]
    public async Task ASetupRunInterruptedByAnExitIsStillRecognisedAtTheNextStartThenForgotten()
    {
        using (HelperLink first = NewLink())
        {
            await first.RefreshAsync();
            await first.LockAsync(PnpWorld.CamerasOnly, "hotkey");
        }

        // BlindsDown closed while its setup run was going on: the run's ID is still in the file, and the run unlocked.
        string id = HelperLink.NewRequestId();
        HelperLink.SavedState saved = HelperLink.ReadState(StateFile);
        saved.Own.Add(new HelperLink.OwnRequest(HelperProtocol.DigestOf(id), HelperCommand.Unlock, Setup: true));
        HelperLink.WriteState(StateFile, saved);
        await OtherProgramAsync(new HelperRequest { Command = HelperCommand.Unlock, RequestId = id, Source = "setup" });

        using HelperLink next = NewLink();
        List<(HelperChangeKind Kind, bool Own)> changes = Record(next);
        await next.RefreshAsync();

        Assert.Equal([(HelperChangeKind.Unlocked, true)], changes);
        Assert.DoesNotContain(File.ReadAllLines(StateFile), l => l.StartsWith("own=S:", StringComparison.Ordinal));
        Assert.False(next.HeldOwnLock);
    }

    [Fact]
    public void AStateFileThatCantBeReadIsSaidNotTakenAsEmpty()
    {
        File.WriteAllLines(StateFile, ["cursor=5", "held=1"]);
        using (new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            HelperLink.SavedState locked = HelperLink.ReadState(StateFile);
            Assert.True(locked.Unreadable);
        }

        HelperLink.SavedState readable = HelperLink.ReadState(StateFile);
        Assert.False(readable.Unreadable);
        Assert.True(readable.Held);
    }

    private static List<(HelperChangeKind Kind, bool Own)> Record(HelperLink link)
    {
        var changes = new List<(HelperChangeKind Kind, bool Own)>();
        link.ChangesReceived += batch => changes.AddRange(batch.Changes.Select(c => (c.Change.Kind, c.Own)));
        return changes;
    }

    private HelperLink NewLink(Func<ElevatedTask, IWin32Window?, string?, Task<int?>>? launch = null) =>
        new(
            StateFile,
            new HelperClient(_pipe, requireSystemOwner: false),
            () => new HelperInstallInfo(Installed: true, Running: true, InstalledVersion: AppVersion.Text),
            launch ?? ((_, _, _) => throw new InvalidOperationException("No setup run expected in this test.")));

    private async Task OtherProgramAsync(HelperRequest request)
    {
        HelperResponse response = await SendAsync(request);
        Assert.True(response.Ok, response.Error);
    }

    private async Task<HelperResponse> SendAsync(HelperRequest request)
    {
        var client = new HelperClient(_pipe, requireSystemOwner: false);
        return await client.SendAsync(request, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
