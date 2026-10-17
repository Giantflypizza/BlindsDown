using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

public sealed class JournalStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void JournalRoundTripsEveryField()
    {
        var store = new FileJournalStore(_temp.File("lock-journal.json"));
        LockJournal journal = SampleJournal();

        store.Save(journal);
        LockJournal loaded = store.Load()!;

        Assert.Equal(journal.LockId, loaded.LockId);
        Assert.Equal(LockPhase.Locked, loaded.Phase);
        Assert.Equal(journal.StartedAt, loaded.StartedAt);
        Assert.True(loaded.ExitedCleanlyWhileLocked);
        LockAction action = Assert.Single(loaded.Actions);
        Assert.Equal("{0.0.1.00000000}.{A}", action.TargetId);
        Assert.Equal("Microphone (Arctis)", action.DisplayName);
        Assert.Equal(ActionStatus.RevertPending, action.Status);
        Assert.True(action.WasAlreadyLocked);
        Assert.Equal(3, action.GuardCount);
        Assert.True(action.AddedWhileLocked);
        Assert.Equal("note", action.Note);
        MicEndpoint mic = Assert.Single(loaded.Before!.Mics);
        Assert.Equal(journal.Before!.Mics[0], mic);
        Assert.Equal("mic|USB\\VID_1038&PID_12E0&MI_00|MICROPHONE (ARCTIS)", mic.StableKey);
        Assert.Equal(journal.Before.Defaults, loaded.Before.Defaults);
    }

    [Fact]
    public void EnumsAreStoredAsReadableText()
    {
        string path = _temp.File("lock-journal.json");
        new FileJournalStore(path).Save(SampleJournal());

        string json = File.ReadAllText(path);

        Assert.Contains("\"phase\": \"Locked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"RevertPending\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"Active\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stableKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isVirtual", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingJournalLoadsAsNull()
    {
        Assert.Null(new FileJournalStore(_temp.File("lock-journal.json")).Load());
    }

    [Fact]
    public void DeleteRemovesTheJournalAndItsBackups()
    {
        string path = _temp.File("lock-journal.json");
        var store = new FileJournalStore(path);
        store.Save(SampleJournal());
        store.Save(SampleJournal());

        store.Delete();

        Assert.Null(store.Load());
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void CorruptJournalFallsBackToTheBackup()
    {
        string path = _temp.File("lock-journal.json");
        var store = new FileJournalStore(path);
        LockJournal older = SampleJournal();
        store.Save(older);
        store.Save(SampleJournal());
        File.WriteAllText(path, "{ \"lockId\": \"not a guid");

        Assert.Equal(older.LockId, store.Load()!.LockId);
    }

    [Fact]
    public void FullyWrittenTempCopyIsUsedWhenTheFileIsMissing()
    {
        string path = _temp.File("lock-journal.json");
        var store = new FileJournalStore(path);
        LockJournal journal = SampleJournal();
        store.Save(journal);
        File.Move(path, path + ".tmp");

        Assert.Equal(journal.LockId, store.Load()!.LockId);
    }

    [Fact]
    public void EveryCopyCorruptThrows()
    {
        string path = _temp.File("lock-journal.json");
        File.WriteAllText(path, "garbage");
        File.WriteAllText(path + ".bak", "{\"phase\": 42");

        Assert.Throws<JournalCorruptException>(() => new FileJournalStore(path).Load());
    }

    [Fact]
    public void JsonThatIsNotAJournalObjectThrows()
    {
        string path = _temp.File("lock-journal.json");
        File.WriteAllText(path, "null");

        Assert.Throws<JournalCorruptException>(() => new FileJournalStore(path).Load());
    }

    [Fact]
    public void HalfWrittenFirstJournalIsNoJournal()
    {
        // Killed while writing the very first save of a lock: the save never returned, so no device
        // was changed yet and there is nothing to recover. That must not look like a corrupt journal.
        string path = _temp.File("lock-journal.json");
        File.WriteAllText(path + ".tmp", "{\n  \"version\": 1,\n  \"lockId\": \"6f1d2b4e-8a43");

        Assert.Null(new FileJournalStore(path).Load());
    }

    [Fact]
    public void NullActionsAreRepaired()
    {
        string path = _temp.File("lock-journal.json");
        File.WriteAllText(path, "{ \"phase\": \"Locked\", \"actions\": null }");
        Assert.Empty(new FileJournalStore(path).Load()!.Actions);

        File.WriteAllText(path, "{ \"phase\": \"Locked\", \"actions\": [ null, { \"targetId\": \"x\", \"status\": \"Applied\" } ] }");
        LockAction action = Assert.Single(new FileJournalStore(path).Load()!.Actions);
        Assert.Equal("x", action.TargetId);
    }

    [Fact]
    public void HandEditedJournalWithCommentsAndTrailingCommasLoads()
    {
        string path = _temp.File("lock-journal.json");
        File.WriteAllText(path, "{ // edited\n \"phase\": \"Unlocking\", \"actions\": [], }");

        Assert.Equal(LockPhase.Unlocking, new FileJournalStore(path).Load()!.Phase);
    }

    [Fact]
    public void PendingRestoresRoundTripAndEmptyDeletesTheFile()
    {
        string path = _temp.File("pending-restores.json");
        var store = new FilePendingRestoreStore(path);
        Assert.Empty(store.Load());

        store.Save([new LockAction { TargetId = "a", Status = ActionStatus.RevertPending, DisplayName = "A" }]);
        LockAction loaded = Assert.Single(store.Load());
        Assert.Equal("a", loaded.TargetId);
        Assert.Equal(ActionStatus.RevertPending, loaded.Status);

        store.Save([]);
        Assert.Empty(store.Load());
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void CorruptPendingRestoresFallBackToTheBackupOrNothing()
    {
        string path = _temp.File("pending-restores.json");
        var store = new FilePendingRestoreStore(path);
        store.Save([new LockAction { TargetId = "old" }]);
        store.Save([new LockAction { TargetId = "new" }]);
        File.WriteAllText(path, "[{\"targetId\": ");

        Assert.Equal("old", Assert.Single(store.Load()).TargetId);

        File.WriteAllText(path + ".bak", "not json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void NullPendingEntriesAreDropped()
    {
        string path = _temp.File("pending-restores.json");
        File.WriteAllText(path, "[null, {\"targetId\": \"a\"}]");

        Assert.Equal("a", Assert.Single(new FilePendingRestoreStore(path).Load()).TargetId);
    }

    [Fact]
    public void MemoryStoresBehaveLikeFiles()
    {
        var journal = new MemoryJournalStore();
        Assert.Null(journal.Load());
        journal.Save(SampleJournal());
        Assert.Equal(1, journal.SaveCount);
        Assert.NotNull(journal.Load());

        // Loads are copies, like reading the file again.
        Assert.NotSame(journal.Load(), journal.Load());
        journal.Corrupt = true;
        Assert.Throws<JournalCorruptException>(journal.Load);
        journal.Corrupt = false;
        journal.Delete();
        Assert.Null(journal.Load());

        var pending = new MemoryPendingRestoreStore();
        Assert.Empty(pending.Load());
        pending.Save([new LockAction { TargetId = "a" }]);
        Assert.Single(pending.Load());
    }

    private static LockJournal SampleJournal() => new()
    {
        LockId = Guid.NewGuid(),
        Phase = LockPhase.Locked,
        StartedAt = FakeClock.Epoch,
        ExitedCleanlyWhileLocked = true,
        Before = new AudioSnapshot(
            [
                new MicEndpoint
                {
                    Id = "{0.0.1.00000000}.{A}",
                    Name = "Microphone (Arctis)",
                    EndpointDescription = "Microphone",
                    State = EndpointState.Active,
                    Muted = false,
                    HasHardwareMute = true,
                    HardwareInstanceId = @"USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000",
                },
            ],
            new DefaultMics("{0.0.1.00000000}.{A}", null, "{0.0.1.00000000}.{A}"),
            FakeClock.Epoch),
        Actions =
        [
            new LockAction
            {
                Kind = ActionKind.MuteMic,
                TargetId = "{0.0.1.00000000}.{A}",
                DisplayName = "Microphone (Arctis)",
                WasAlreadyLocked = true,
                Status = ActionStatus.RevertPending,
                Note = "note",
                GuardCount = 3,
                AddedWhileLocked = true,
            },
        ],
    };
}
