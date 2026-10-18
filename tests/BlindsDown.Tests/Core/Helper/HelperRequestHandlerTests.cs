using System.Text;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Tests.Core.Fakes;
using BlindsDown.Tests.Core.Pnp;

namespace BlindsDown.Tests.Core.Helper;

public sealed class HelperRequestHandlerTests
{
    [Fact]
    public void HelloAnswersWithTheVersionTheStatusAndTheNewestEventId()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Hello, ClientVersion = "0.2.0" });

        Assert.True(response.Ok);
        Assert.Null(response.Error);
        Assert.Equal(HelperProtocol.Version, response.V);
        Assert.Equal("0.2.0-helper", response.HelperVersion);
        Assert.Equal(LockState.Unlocked, response.Status!.State);
        Assert.Equal(world.Devices.All.Count, response.Status.Devices.Count);
        Assert.Equal(0, response.LastEventId);
        Assert.Empty(response.Events);
        Assert.Null(response.Report);
    }

    [Fact]
    public void LockLocksTheDevicesAndReturnsTheStatusAndItsAlerts()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        world.Devices["busy"].DisableAlways = PnpChangeResult.Vetoed;

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything });

        Assert.True(response.Ok);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(LockState.PartiallyLocked, response.Status!.State);
        HelperEvent busy = Assert.Single(response.Events);
        Assert.Equal(1, busy.Id);
        Assert.Equal(EngineAlertKind.DeviceBusy, busy.Kind);
        Assert.Equal("Camera busy", busy.DeviceName);
        Assert.True(busy.Urgent);

        // The lock itself is a change with the next ID (alerts and changes share the numbering).
        HelperChange locked = Assert.Single(response.Changes);
        Assert.Equal(HelperChangeKind.Locked, locked.Kind);
        Assert.Equal(2, locked.Id);
        Assert.Equal(2, response.LastEventId);
    }

    [Fact]
    public void ALockRequestThatTurnsCamerasBackOnIsAnUrgentEventAndIsLoggedWithItsSource()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        var log = new List<string>();
        handler.Logged += log.Add;
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, Source = "hotkey" });
        Assert.Empty(log);

        // Any program on the PC can write this to the pipe; it can only make the helper do less, but never quietly.
        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest { MicHardware = true }, Source = "some tool" });

        Assert.True(response.Ok);
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.Equal(LockState.Locked, response.Status!.State);
        HelperEvent released = Assert.Single(response.Events, e => e.Kind == EngineAlertKind.ReEnabledWhileLocked);
        Assert.True(released.Urgent);
        Assert.Contains("Camera cam1, Camera busy", released.Message, StringComparison.Ordinal);
        Assert.Equal("After a lock request from \"some tool\", these devices are no longer turned off: Camera cam1, Camera busy.", Assert.Single(log));
    }

    [Fact]
    public void ALockRequestWithoutASourceIsLoggedAsSuch()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        var log = new List<string>();
        handler.Logged += log.Add;
        world.Engine.Lock(PnpWorld.CamerasOnly);

        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest() });

        Assert.StartsWith("After a lock request from a client that didn't say who it is, ", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void UnlockReturnsTheRestoreReport()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything });

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, Source = "hotkey" });

        Assert.True(response.Ok);
        Assert.False(world.Devices["cam1"].Disabled);
        Assert.True(response.Report!.CoversCameras);
        Assert.True(response.Report.AllExact);
        Assert.Equal(LockState.Unlocked, response.Status!.State);
    }

    [Fact]
    public void UnlockWhenNotLockedHasNoReport()
    {
        (_, HelperRequestHandler handler) = Setup();

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock });

        Assert.True(response.Ok);
        Assert.Null(response.Report);
    }

    [Fact]
    public void EventsHaveIncreasingIdsAndOnlyNewerOnesAreSent()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything });
        for (int i = 0; i < 3; i++)
        {
            world.Devices.SomeoneEnables("cam1");
            world.Pump();
            world.Clock.AdvanceSeconds(31);
        }

        HelperResponse all = handler.Handle(new HelperRequest { Command = HelperCommand.Status });
        HelperResponse newer = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 3 });
        HelperResponse none = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = all.LastEventId });

        // ID 1 is the lock (a change), then the three alerts.
        Assert.Equal(1, Assert.Single(all.Changes).Id);
        Assert.Equal([2L, 3L, 4L], all.Events.Select(e => e.Id));
        Assert.All(all.Events, e => Assert.Equal(EngineAlertKind.ReEnabledWhileLocked, e.Kind));
        Assert.Equal(4, all.LastEventId);
        Assert.Equal([4L], newer.Events.Select(e => e.Id));
        Assert.Empty(newer.Changes);
        Assert.Empty(none.Events);
        Assert.Equal(4, none.LastEventId);
    }

    [Fact]
    public void OnlyTheLastHundredEventsAreKeptAndAtMostFiftySentAtOnce()
    {
        var world = new PnpWorld();
        for (int i = 0; i < 120; i++)
        {
            world.Devices.Add("c" + i).DisableAlways = PnpChangeResult.Vetoed;
        }

        world.Engine.Initialize();
        var handler = new HelperRequestHandler(world.Engine, "v", lastEventId: 0);

        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly });
        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Status });
        HelperResponse older = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 30 });

        Assert.Equal(121, response.LastEventId); // 120 alerts, then the lock (a change).
        Assert.Equal(HelperRequestHandler.MaxEventsPerResponse, response.Events.Count);
        Assert.Equal(Enumerable.Range(71, 50).Select(i => (long)i), response.Events.Select(e => e.Id));
        Assert.Equal(71, older.Events[0].Id); // Event 21..30 are no longer kept; the newest 50 are sent.
    }

    [Fact]
    public void EventIdsKeepIncreasingWhenTheHelperRestarts()
    {
        (PnpWorld world, _) = Setup();
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var handler = new HelperRequestHandler(world.Engine, "v");
        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Hello });

        Assert.True(response.LastEventId >= before);
    }

    [Fact]
    public void AnEventIdNewerThanAnyThisHelperRaisedStillGetsTheNewAlerts()
    {
        // The tray app's ID comes from an earlier run of the helper whose clock was ahead.
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        world.Devices["busy"].DisableAlways = PnpChangeResult.Vetoed;
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything });

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 5_000 });

        Assert.Equal(1, Assert.Single(response.Events).Id);
        Assert.Equal(2, Assert.Single(response.Changes).Id);
        Assert.Equal(2, response.LastEventId);
    }

    [Fact]
    public void EveryChangeToTheLockCarriesTheIdOfTheRequestThatMadeIt()
    {
        // The tray app tells its own changes from another program's by these IDs: any program running as a signed-in
        // user can send the helper an unlock or a lock that covers less.
        (PnpWorld world, HelperRequestHandler handler) = Setup();

        HelperResponse locked = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-1", Source = "hotkey" });
        HelperResponse same = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-2", SinceEvent = locked.LastEventId });
        HelperResponse narrowed = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest { MicHardware = true }, RequestId = "other-1", Source = "some tool", SinceEvent = same.LastEventId });
        HelperResponse unlocked = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "other-2", SinceEvent = narrowed.LastEventId });
        HelperResponse nothing = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "other-3", SinceEvent = unlocked.LastEventId });

        HelperChange first = Assert.Single(locked.Changes);
        Assert.Equal((HelperChangeKind.Locked, HelperProtocol.DigestOf("app-1"), "hotkey"), (first.Kind, first.RequestDigest, first.Source));
        Assert.Empty(same.Changes); // The same request changes nothing.
        HelperChange less = Assert.Single(narrowed.Changes);
        Assert.Equal((HelperChangeKind.Narrowed, HelperProtocol.DigestOf("other-1"), "some tool"), (less.Kind, less.RequestDigest, less.Source));
        Assert.Contains("Camera cam1", less.TurnedOn);
        HelperChange end = Assert.Single(unlocked.Changes);
        Assert.Equal((HelperChangeKind.Unlocked, HelperProtocol.DigestOf("other-2")), (end.Kind, end.RequestDigest));
        Assert.Empty(nothing.Changes); // Nothing was locked.
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ChangesAreKeptForAClientThatAsksLater()
    {
        (_, HelperRequestHandler handler) = Setup();
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });
        handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "b" });

        HelperResponse later = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 0 });

        Assert.Equal([HelperProtocol.DigestOf("a"), HelperProtocol.DigestOf("b")], later.Changes.Select(c => c.RequestDigest));
    }

    [Fact]
    public void TheRequestIdItselfIsNeverSentToAnyClient()
    {
        // Every pipe client can read the change log: with the ID itself, another program could send it again and pass
        // its own unlock off as the app's. Only a one-way fingerprint goes out.
        (_, HelperRequestHandler handler) = Setup();
        string id = Guid.NewGuid().ToString("N");
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = id });

        byte[] answer = handler.HandleMessage(HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Status }));

        string text = Encoding.UTF8.GetString(answer);
        Assert.DoesNotContain(id, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(HelperProtocol.DigestOf(id), text, StringComparison.Ordinal);
        Assert.Equal(64, HelperProtocol.DigestOf(id).Length);
    }

    [Fact]
    public void ARequestIdThatWasAlreadyUsedIsRefusedAndChangesNothing()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-1" });
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-2" }); // No change.

        HelperResponse replayedUnlock = handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "app-1" });
        HelperResponse replayedLock = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, RequestId = "app-2" });

        Assert.False(replayedUnlock.Ok);
        Assert.Equal("That request ID was already used.", replayedUnlock.Error);
        Assert.False(replayedLock.Ok);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.True(world.Devices["mic1"].Disabled);
        Assert.True(handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "app-3" }).Ok);
        Assert.False(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ANewRunStartsAboveEveryIdAnEarlierRunHandedOutAlertsIncluded()
    {
        // Alerts aren't kept across a restart, but a client may have taken its position from one: a later run whose IDs
        // started below it would have its first changes skipped.
        var store = new MemoryChangeStore();
        (PnpWorld world, _) = Setup();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });
        world.Devices.SomeoneEnables("cam1"); // The guard turns it off again and alerts: an ID that is no change's.
        world.Pump();
        HelperResponse alerted = first.Handle(new HelperRequest { Command = HelperCommand.Status });
        Assert.True(alerted.LastEventId > alerted.Changes.Max(c => c.Id));
        Assert.Equal(alerted.LastEventId, store.Log.HighestId);

        // The clock went back: the next run still starts above the alert's ID.
        var second = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 5, store);
        HelperResponse next = second.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "b", SinceEvent = alerted.LastEventId });

        Assert.False(next.SinceReset);
        HelperChange change = Assert.Single(next.Changes);
        Assert.True(change.Id > alerted.LastEventId);
    }

    [Fact]
    public void ARunThatRecordsNothingStillKeepsLaterRunsAboveTheIdItHandedOut()
    {
        // A run whose clock was ahead hands out its start ID with its first answer, even if it records nothing.
        var store = new MemoryChangeStore();
        (PnpWorld world, _) = Setup();
        var ahead = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 9_000, store);
        HelperResponse seen = ahead.Handle(new HelperRequest { Command = HelperCommand.Status });

        var later = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        HelperResponse next = later.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "x", SinceEvent = seen.LastEventId });
        HelperResponse changed = later.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "y", SinceEvent = seen.LastEventId });

        Assert.True(store.Log.HighestId >= 9_000); // Saved when the run started, though it recorded nothing.
        Assert.True(next.RunId >= 9_000);
        Assert.Single(changed.Changes); // Its change comes after the position the client took from the earlier run.
    }

    [Fact]
    public void TheChangeLogAndTheUsedIdsSurviveARestartOfTheHelper()
    {
        // A restart (an update, Windows Update at night) must not hide an unlock from a tray app that wasn't running.
        var store = new MemoryChangeStore();
        (PnpWorld world, _) = Setup();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-1", Source = "hotkey" });
        HelperResponse unlocked = first.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "other-1", Source = "tool" });

        // The clock went back meanwhile: the IDs still continue after the last one kept.
        var second = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 5, store);
        HelperResponse later = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 1_000 });
        HelperResponse replayed = second.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "other-1" });
        HelperResponse next = second.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "app-2", SinceEvent = later.LastEventId });

        Assert.Equal([HelperChangeKind.Locked, HelperChangeKind.Unlocked], later.Changes.Select(c => c.Kind));
        HelperChange change = later.Changes[^1];
        Assert.Equal((HelperProtocol.DigestOf("other-1"), "tool"), (change.RequestDigest, change.Source));
        Assert.Contains("Camera cam1", change.TurnedOn);
        Assert.Equal(unlocked.LastEventId, later.LastEventId);
        Assert.False(replayed.Ok);
        Assert.True(Assert.Single(next.Changes).Id > unlocked.LastEventId);
    }

    [Fact]
    public void TheChangeLogFileRoundTrips()
    {
        using var folder = new TempDirectory();
        var store = new FileHelperChangeStore(folder.File("lock-changes.json"));
        var at = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);
        HelperChange[] changes =
        [
            new(5, HelperChangeKind.Locked, HelperProtocol.DigestOf("a"), "hotkey", [], [], at),
            new(6, HelperChangeKind.Narrowed, null, null, ["Camera"], ["Busy camera"], at.AddMinutes(1)),
        ];

        HelperChangeLog none = store.Load(); // No file yet.
        store.Save(new HelperChangeLog(changes, HelperProtocol.DigestOf("b"), "settings"));
        HelperChangeLog loaded = store.Load();

        Assert.Empty(none.Changes);
        Assert.Null(none.LastLockDigest);
        Assert.Equal(changes.Select(Text), loaded.Changes.Select(Text));
        Assert.Equal((HelperProtocol.DigestOf("b"), "settings"), (loaded.LastLockDigest, loaded.LastLockSource));

        // Without a last lock request (after an unlock).
        store.Save(new HelperChangeLog(changes));
        Assert.Equal((null, null), (store.Load().LastLockDigest, store.Load().LastLockSource));

        static string Text(HelperChange c) => $"{c.Id}|{c.Kind}|{c.RequestDigest}|{c.Source}|{string.Join(",", c.TurnedOn)}|{string.Join(",", c.Released)}|{c.At:O}";
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")] // An array: the log's earlier shape.
    [InlineData("""{"changes":null}""")]
    [InlineData("""{"changes":[null,{"id":7,"kind":"Locked","turnedOn":[],"at":"2026-10-18T09:30:00+00:00"},{"id":8,"kind":42,"turnedOn":[],"released":[],"at":"2026-10-18T09:30:00+00:00"}]}""")]
    public void AChangeLogFileThatCantBeUsedGivesAnEmptyLog(string text)
    {
        using var folder = new TempDirectory();
        string path = folder.File("lock-changes.json");
        File.WriteAllText(path, text);

        HelperChangeLog log = new FileHelperChangeStore(path).Load();

        Assert.Empty(log.Changes);
        Assert.Null(log.LastLockDigest);
    }

    [Fact]
    public void AChangeLogFileKeepsWhatItCanUse()
    {
        using var folder = new TempDirectory();
        string path = folder.File("lock-changes.json");
        string digest = HelperProtocol.DigestOf("a");
        File.WriteAllText(path, $$"""{"changes":[null,{"id":9,"kind":"Unlocked","turnedOn":["Camera"],"released":[],"at":"2026-10-18T09:30:00+00:00"}],"lastLockDigest":"{{digest}}","futureThing":1}""");

        HelperChangeLog log = new FileHelperChangeStore(path).Load();

        HelperChange change = Assert.Single(log.Changes);
        Assert.Equal((9L, HelperChangeKind.Unlocked, "Camera"), (change.Id, change.Kind, change.TurnedOn[0]));
        Assert.Equal(digest, log.LastLockDigest);
        Assert.Null(log.LastLockSource);
    }

    [Fact]
    public void TheLastLockRequestIsKeptWithTheChangeLogUntilAnUnlock()
    {
        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var handler = new HelperRequestHandler(world.Engine, "v", lastEventId: 0, store);

        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a", Source = "hotkey" });
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "b", Source = "tray" });
        HelperChangeLog locked = store.Log;
        handler.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "c" });
        HelperChangeLog unlocked = store.Log;

        // The second request changed nothing, but it is the last lock request now.
        Assert.Single(locked.Changes);
        Assert.Equal((HelperProtocol.DigestOf("b"), "tray"), (locked.LastLockDigest, locked.LastLockSource));
        Assert.Equal(2, unlocked.Changes.Count);
        Assert.Equal((null, null), (unlocked.LastLockDigest, unlocked.LastLockSource));
    }

    [Fact]
    public void ANarrowingFinishedAfterARestartOfTheHelperIsStillTheLastLockRequests()
    {
        // The app's own narrower lock (e.g. a camera excluded in Settings) can't release a device Windows doesn't list
        // right now; the helper restarts before it can. Once the device is listed, the release is still the app's.
        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        HelperResponse locked = first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "lock-1", Source = "hotkey" });
        world.Devices.EnumerateThrows = true;
        HelperResponse narrower = first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest { MicHardware = true }, RequestId = "a", Source = "settings", SinceEvent = locked.LastEventId });
        Assert.True(narrower.Ok);
        Assert.Empty(narrower.Changes);
        Assert.True(world.Devices["cam1"].Disabled);

        (DeviceLockEngine engine, HelperRequestHandler second) = RestartHelper(world, lastEventId: 2_000, store);
        world.Devices.EnumerateThrows = false;
        engine.Reconcile();
        HelperResponse later = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 2_000 });

        Assert.False(world.Devices["cam1"].Disabled);
        HelperChange change = Assert.Single(later.Changes);
        Assert.Equal((HelperChangeKind.Narrowed, HelperProtocol.DigestOf("a"), "settings"), (change.Kind, change.RequestDigest, change.Source));
        Assert.Contains("Camera cam1", change.Released);
    }

    [Fact]
    public void TheLastLockRequestsIdCantBeUsedAgainAfterARestartAlsoWhenItChangedNothing()
    {
        // Only its fingerprint shows the app a later narrowing is its own, so nobody may send it again.
        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "b" }); // No change.

        (_, HelperRequestHandler second) = RestartHelper(world, lastEventId: 2_000, store);
        HelperResponse replayed = second.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "b" });

        Assert.False(replayed.Ok);
        Assert.Equal("That request ID was already used.", replayed.Error);
        Assert.True(world.Devices["cam1"].Disabled);
    }

    [Fact]
    public void ANarrowingARestartInterruptedIsTheRequestsWhenTheHelperFinishesIt()
    {
        // The helper stops while a lock request turns cameras back on; its next start finishes the change while the
        // engine starts, before any request.
        var micOnly = new DeviceLockRequest { MicHardware = true };
        PnpWorld probe = NewWorld();
        probe.Engine.Lock(PnpWorld.Everything);
        probe.Crash.Record();
        probe.Engine.Lock(micOnly);
        int beforeEnable = probe.Crash.Seen.FindIndex(p => p.StartsWith("before enable", StringComparison.Ordinal)) + 1;
        Assert.True(beforeEnable > 0);

        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "lock-1", Source = "hotkey" });
        world.Crash.ArmAt(beforeEnable);
        HelperResponse stopped = first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = micOnly, RequestId = "a", Source = "settings" });
        Assert.False(stopped.Ok); // The simulated crash ends the request half-way.
        Assert.True(world.Devices["cam1"].Disabled);

        (_, HelperRequestHandler second) = RestartHelper(world, lastEventId: 2_000, store);
        HelperResponse later = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 2_000 });

        Assert.False(world.Devices["cam1"].Disabled);
        HelperChange change = Assert.Single(later.Changes);
        Assert.Equal((HelperChangeKind.Narrowed, HelperProtocol.DigestOf("a"), "settings"), (change.Kind, change.RequestDigest, change.Source));
        Assert.Contains("Camera cam1", change.Released);
    }

    [Fact]
    public void AClientAheadOfARestartedHelperGetsOnlyTheNewRunsChanges()
    {
        // The earlier run's clock was ahead: the client's position is past everything this run has. It gets this run's
        // changes, and not the earlier run's again (it saw them).
        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });
        first.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "b" });

        (_, HelperRequestHandler second) = RestartHelper(world, lastEventId: 2_000, store);
        second.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "c" });
        HelperResponse ahead = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 99_999 });

        Assert.True(ahead.SinceReset);
        HelperChange change = Assert.Single(ahead.Changes);
        Assert.Equal((2_001L, HelperProtocol.DigestOf("c")), (change.Id, change.RequestDigest));
        Assert.Equal(2_001, ahead.LastEventId);
        Assert.Equal(2_000, ahead.RunId);
    }

    [Fact]
    public void AClientWhosePositionIsFromAnotherRunGetsAllOfThisRunsChanges()
    {
        // The clock went back a little since the earlier run, whose last alerts (not kept across a restart) took the
        // client's position past where this run starts. This run hands out IDs past that position before the client
        // asks again: only the run ID shows the position is not one of this run's.
        var store = new MemoryChangeStore();
        PnpWorld world = NewWorld();
        var first = new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 1_000, store);
        first.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });
        for (int i = 0; i < 3; i++)
        {
            world.Devices.SomeoneEnables("cam1");
            world.Pump();
            world.Clock.AdvanceSeconds(31);
        }

        HelperResponse seen = first.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 1_000 });
        Assert.Equal((1_004L, 1_000L), (seen.LastEventId, seen.RunId)); // The lock, then three alerts.

        // Each run normally starts above every ID the last one handed out; here that record was lost (a save failed).
        store.Save(store.Log with { HighestId = 0 });
        (_, HelperRequestHandler second) = RestartHelper(world, lastEventId: 1_002, store);
        second.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "b" });
        second.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "c" });
        second.Handle(new HelperRequest { Command = HelperCommand.Unlock, RequestId = "d" });
        HelperResponse behind = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = seen.LastEventId, SinceRun = seen.RunId });
        HelperResponse inStep = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 1_004, SinceRun = behind.RunId });
        HelperResponse unknownRun = second.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 1_004 });

        Assert.Equal(1_002, behind.RunId);
        Assert.True(behind.SinceReset);
        Assert.Equal([1_003L, 1_004L, 1_005L], behind.Changes.Select(c => c.Id));
        Assert.Equal(new[] { "b", "c", "d" }.Select(HelperProtocol.DigestOf), behind.Changes.Select(c => c.RequestDigest));
        Assert.Equal(1_005, behind.LastEventId);

        // A position from this run is compared as it is.
        Assert.False(inStep.SinceReset);
        Assert.Equal([1_005L], inStep.Changes.Select(c => c.Id));

        // So is one from a client that doesn't say which run it is from.
        Assert.False(unknownRun.SinceReset);
        Assert.Equal([1_005L], unknownRun.Changes.Select(c => c.Id));
    }

    [Fact]
    public void EveryAnswerSaysWhichRunOfTheHelperMadeIt()
    {
        PnpWorld world = NewWorld();
        var handler = new HelperRequestHandler(world.Engine, "v", lastEventId: 3_000);

        HelperResponse hello = handler.Handle(new HelperRequest { Command = HelperCommand.Hello });
        HelperResponse locked = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything });
        HelperResponse refused = handler.Handle(new HelperRequest { Command = HelperCommand.Lock });
        HelperResponse malformed = ParseResponse(handler.HandleMessage("{"u8.ToArray()));

        Assert.All(new[] { hello, locked, refused, malformed }, r => Assert.Equal(3_000, r.RunId));
        Assert.False(malformed.Ok);
    }

    [Fact]
    public void AClientWhosePositionIsAheadOfTheHelperIsToldTheAnswerStartsOver()
    {
        (_, HelperRequestHandler handler) = Setup();
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });

        HelperResponse ahead = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 99_999 });
        HelperResponse inStep = handler.Handle(new HelperRequest { Command = HelperCommand.Status, SinceEvent = ahead.LastEventId });

        Assert.True(ahead.SinceReset);
        Assert.Single(ahead.Changes);
        Assert.False(inStep.SinceReset);
        Assert.Empty(inStep.Changes);
    }

    [Fact]
    public void ChangesRecordWhenTheyHappenedAndWhatTheLockStoppedCovering()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();
        world.Devices["busy"].DisableAlways = PnpChangeResult.Vetoed;
        DateTimeOffset before = DateTimeOffset.UtcNow;
        HelperResponse locked = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.Everything, RequestId = "a" });

        // A lock that leaves the cameras out: one camera is turned back on, the busy one (never off) is just released.
        HelperResponse narrowed = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = new DeviceLockRequest { MicHardware = true }, RequestId = "b", SinceEvent = locked.LastEventId });

        HelperChange change = Assert.Single(narrowed.Changes);
        Assert.Equal(HelperChangeKind.Narrowed, change.Kind);
        Assert.Equal(["Camera cam1"], change.TurnedOn);
        Assert.Contains("Camera busy", change.Released);
        Assert.InRange(change.At, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ünï")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void ARequestIdMustBeShortLettersDigitsOrDashes(string id)
    {
        byte[] request = HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Unlock, RequestId = id });

        Assert.Null(HelperMessages.ParseRequest(request, out string? error));
        Assert.Contains("request ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BadRequestsGetAnErrorNotAnException()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();

        HelperResponse noOptions = handler.Handle(new HelperRequest { Command = HelperCommand.Lock });
        HelperResponse wrongVersion = handler.Handle(new HelperRequest { V = 3, Command = HelperCommand.Unlock });
        HelperResponse unknown = handler.Handle(new HelperRequest { Command = (HelperCommand)42 });
        HelperResponse nothing = handler.Handle(null!);

        Assert.False(noOptions.Ok);
        Assert.Equal("A lock request needs lock options.", noOptions.Error);
        Assert.False(wrongVersion.Ok);
        Assert.Contains("version", wrongVersion.Error, StringComparison.Ordinal);
        Assert.False(unknown.Ok);
        Assert.Equal("Unknown command.", unknown.Error);
        Assert.False(nothing.Ok);
        Assert.Empty(world.Devices.DisableCalls);
        Assert.Empty(world.Devices.EnableCalls);
        Assert.All(new[] { noOptions, wrongVersion, unknown, nothing }, r => Assert.Equal("0.2.0-helper", r.HelperVersion));
    }

    [Fact]
    public void AnEngineFailureBecomesAnErrorResponse()
    {
        var engine = new DeviceLockEngine(new ExplodingBackend(), new MemoryDeviceJournalStore(), new FakeClock());
        var handler = new HelperRequestHandler(engine, "v", lastEventId: 0);

        HelperResponse response = handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly });

        Assert.False(response.Ok);
        Assert.Equal("The helper failed: Bug in the backend.", response.Error);
        Assert.Equal("v", response.HelperVersion);
    }

    [Fact]
    public void HandleMessageRefusesMalformedBytesWithAParsableAnswer()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();

        foreach (string bad in new[] { string.Empty, "{", """{"v":1,"command":"Lock","lock":{"cameras":true,"disable":["PCI\\VEN_8086"]}}""", """{"v":9,"command":"Unlock"}""" })
        {
            HelperResponse response = ParseResponse(handler.HandleMessage(Encoding.UTF8.GetBytes(bad)));
            Assert.False(response.Ok);
            Assert.False(string.IsNullOrEmpty(response.Error));
        }

        Assert.Empty(world.Devices.DisableCalls);
    }

    [Fact]
    public void HandleMessageRunsAValidRequest()
    {
        (PnpWorld world, HelperRequestHandler handler) = Setup();

        byte[] answer = handler.HandleMessage(HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly, Source = "tray" }));

        HelperResponse response = ParseResponse(answer);
        Assert.True(response.Ok);
        Assert.True(world.Devices["cam1"].Disabled);
        Assert.Equal(LockState.Locked, response.Status!.State);
    }

    [Fact]
    public void LongDeviceNamesNeverMakeAnAnswerUnreadable()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1", name: new string('N', 2000));
        world.Engine.Initialize();
        var handler = new HelperRequestHandler(world.Engine, new string('v', 2000), lastEventId: 0);
        world.Devices["cam1"].DisableAlways = PnpChangeResult.Vetoed;

        byte[] answer = handler.HandleMessage(HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly }));

        HelperResponse response = ParseResponse(answer);
        Assert.True(response.Ok);
        Assert.Equal(HelperProtocol.MaxStringLength, response.Status!.Devices[0].Name.Length);
        Assert.Equal(HelperProtocol.MaxStringLength, response.HelperVersion.Length);
        Assert.Single(response.Events);
    }

    [Fact]
    public void AnAnswerTooLargeForThePipeDropsTheOldestEvents()
    {
        var world = new PnpWorld();
        for (int i = 0; i < 10; i++)
        {
            world.Devices.Add("c" + i, name: $"{i} " + new string('N', 600));
        }

        world.Engine.Initialize();
        var handler = new HelperRequestHandler(world.Engine, "v", lastEventId: 0);
        handler.Handle(new HelperRequest { Command = HelperCommand.Lock, Lock = PnpWorld.CamerasOnly });
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                world.Devices.SomeoneEnables("c" + i);
            }

            world.Pump();
            world.Clock.AdvanceSeconds(31);
        }

        byte[] answer = handler.HandleMessage(HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Status }));

        Assert.True(answer.Length <= HelperProtocol.MaxMessageBytes);
        HelperResponse response = ParseResponse(answer);
        Assert.True(response.Ok);
        Assert.InRange(response.Events.Count, 1, HelperRequestHandler.MaxEventsPerResponse - 1);
        Assert.Equal(response.LastEventId, response.Events[^1].Id); // The newest are kept.
        Assert.Equal(10, response.Status!.Devices.Count);
    }

    private static (PnpWorld World, HelperRequestHandler Handler) Setup()
    {
        PnpWorld world = NewWorld();
        return (world, new HelperRequestHandler(world.Engine, "0.2.0-helper", lastEventId: 0));
    }

    /// <summary>
    /// The helper service starts again: a new engine over the same devices and lock record, and a new request handler
    /// over the same change log, created before the engine starts as the service does it. <paramref name="lastEventId"/>
    /// stands for the clock.
    /// </summary>
    private static (DeviceLockEngine Engine, HelperRequestHandler Handler) RestartHelper(PnpWorld world, long lastEventId, IHelperChangeStore store)
    {
        world.Crash.Disarm();
        world.Devices.TakeNotifications();
        DeviceLockEngine engine = world.CreateEngine();
        var handler = new HelperRequestHandler(engine, "0.2.0-helper", lastEventId, store);
        engine.Initialize();
        return (engine, handler);
    }

    /// <summary>Two cameras and a microphone, the engine started, no request handler yet.</summary>
    private static PnpWorld NewWorld()
    {
        var world = new PnpWorld();
        world.Devices.Add("cam1");
        world.Devices.Add("busy");
        world.Devices.AddMic("mic1");
        world.Engine.Initialize();
        return world;
    }

    private static HelperResponse ParseResponse(byte[] bytes)
    {
        HelperResponse? response = HelperMessages.ParseResponse(bytes, out string? error);
        Assert.True(response is not null, error);
        return response;
    }

    /// <summary>The change log as a store keeps it (copied in and out, like a file).</summary>
    private sealed class MemoryChangeStore : IHelperChangeStore
    {
        public HelperChangeLog Log { get; private set; } = HelperChangeLog.Empty;

        public HelperChangeLog Load() => Log with { Changes = [.. Log.Changes] };

        public void Save(HelperChangeLog log) => Log = log with { Changes = [.. log.Changes] };
    }

    /// <summary>A backend with a bug: throws something no backend should, which the engine lets through.</summary>
    private sealed class ExplodingBackend : IPnpBackend
    {
        public IReadOnlyList<PnpDevice> Enumerate() => throw new InvalidCastException("Bug in the backend.");

        public PnpChange Disable(string instanceId) => throw new InvalidCastException("Bug in the backend.");

        public PnpChange Enable(string instanceId, bool clearPersistent = false) => throw new InvalidCastException("Bug in the backend.");
    }
}
