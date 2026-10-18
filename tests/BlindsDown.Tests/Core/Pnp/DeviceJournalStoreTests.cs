using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

public sealed class DeviceJournalStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void JournalRoundTripsEveryField()
    {
        var store = new FileDeviceJournalStore(_temp.File("device-lock-journal.json"));
        DeviceLockJournal journal = SampleJournal();

        store.Save(journal);
        DeviceLockJournal loaded = store.Load()!;

        Assert.Equal(journal.LockId, loaded.LockId);
        Assert.Equal(LockPhase.Locked, loaded.Phase);
        Assert.Equal(journal.StartedAt, loaded.StartedAt);
        Assert.True(loaded.Request.Cameras);
        Assert.True(loaded.Request.MicHardware);
        Assert.True(loaded.Request.IncludeInfrared);
        Assert.False(loaded.Request.IncludeVirtual);
        Assert.Equal(journal.Request.Exclusions, loaded.Request.Exclusions);
        Assert.Equal(journal.Request.KeepEnabledInstanceIds, loaded.Request.KeepEnabledInstanceIds);
        Assert.True(loaded.Request.LocksSameAs(journal.Request));
        LockAction action = Assert.Single(loaded.Actions);
        Assert.Equal(ActionKind.DisableDevice, action.Kind);
        Assert.Equal(@"USB\VID_3456&PID_789A&MI_00\6&4E5F6A7B&0&0000", action.TargetId);
        Assert.Equal("HD Webcam C270", action.DisplayName);
        Assert.Equal(ActionStatus.ApplyFailed, action.Status);
        Assert.True(action.WasAlreadyLocked);
        Assert.True(action.AddedWhileLocked);
        Assert.Equal(2, action.GuardCount);
        Assert.Equal("In use by an app; BlindsDown keeps trying.", action.Note);
        Assert.Equal(DeviceKind.Camera, loaded.KindOf(action.TargetId));
        Assert.Equal(DeviceKind.MicrophoneHardware, loaded.KindOf(@"usb\vid_3456&pid_789a&mi_02\6&4e5f6a7b&0&0002"));
    }

    [Fact]
    public void TheFileIsReadableTextWithEnumsAsNames()
    {
        string path = _temp.File("device-lock-journal.json");
        new FileDeviceJournalStore(path).Save(SampleJournal());

        string json = File.ReadAllText(path);

        Assert.Contains("\"phase\": \"Locked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"DisableDevice\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"ApplyFailed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MicrophoneHardware\"", json, StringComparison.Ordinal);
        Assert.Contains("\"keepEnabledInstanceIds\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingJournalLoadsAsNull()
    {
        Assert.Null(new FileDeviceJournalStore(_temp.File("device-lock-journal.json")).Load());
    }

    [Fact]
    public void DeleteRemovesTheJournalAndItsBackups()
    {
        string path = _temp.File("device-lock-journal.json");
        var store = new FileDeviceJournalStore(path);
        store.Save(SampleJournal());
        store.Save(SampleJournal());
        Assert.True(File.Exists(path + ".bak"));

        store.Delete();

        Assert.Null(store.Load());
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void ACorruptJournalFallsBackToTheBackup()
    {
        string path = _temp.File("device-lock-journal.json");
        var store = new FileDeviceJournalStore(path);
        DeviceLockJournal older = SampleJournal();
        store.Save(older);
        store.Save(SampleJournal());
        File.WriteAllText(path, "{ \"phase\": ");

        DeviceLockJournal loaded = store.Load()!;

        Assert.Equal(older.LockId, loaded.LockId);
    }

    [Fact]
    public void AJournalThatCantBeReadAtAllIsReportedAsCorrupt()
    {
        string path = _temp.File("device-lock-journal.json");
        File.WriteAllText(path, "not json");
        File.WriteAllText(path + ".bak", "{ broken");

        Assert.Throws<JournalCorruptException>(() => new FileDeviceJournalStore(path).Load());
    }

    [Fact]
    public void AHalfWrittenFirstSaveMeansNoLock()
    {
        string path = _temp.File("device-lock-journal.json");
        File.WriteAllText(path + ".tmp", "{ \"lockId\": \"");

        Assert.Null(new FileDeviceJournalStore(path).Load());
    }

    [Fact]
    public void DamagedEntriesAreDroppedAndMissingPartsFilledIn()
    {
        string path = _temp.File("device-lock-journal.json");
        File.WriteAllText(
            path,
            """
            {
              // A hand-edited file.
              "lockId": "6c7f1d43-8a55-4d0e-9a5e-41c1f8b7b3a1",
              "phase": "Locked",
              "request": { "cameras": true, "exclusions": null, "keepEnabledInstanceIds": null },
              "actions": [
                null,
                { "kind": "DisableDevice", "targetId": "", "status": "Applied" },
                { "kind": "MuteMic", "targetId": "{0.0.1.00000000}.{a}", "status": "Applied" },
                { "kind": "DisableDevice", "targetId": "USB\\VID_1&PID_2\\3", "displayName": null, "status": "Applied" },
              ],
              "deviceKinds": null,
            }
            """);

        DeviceLockJournal journal = new FileDeviceJournalStore(path).Load()!;

        LockAction action = Assert.Single(journal.Actions);
        Assert.Equal(@"USB\VID_1&PID_2\3", action.TargetId);
        Assert.Equal(@"USB\VID_1&PID_2\3", action.DisplayName);
        Assert.Empty(journal.Request.Exclusions);
        Assert.Empty(journal.Request.KeepEnabledInstanceIds);
        Assert.NotNull(journal.DeviceKinds);
        Assert.Equal(DeviceKind.Camera, journal.KindOf(action.TargetId));
    }

    [Fact]
    public void RecordedKindsOtherThanCamerasAndMicrophoneHardwareAreForgotten()
    {
        string path = _temp.File("device-lock-journal.json");
        File.WriteAllText(
            path,
            """
            {
              "phase": "Locked",
              "actions": [
                { "kind": "DisableDevice", "targetId": "USB\\A", "status": "Applied" },
                { "kind": "DisableDevice", "targetId": "USB\\B", "status": "Applied" },
                { "kind": "DisableDevice", "targetId": "USB\\C", "status": "Applied" }
              ],
              "deviceKinds": { "USB\\A": "MicrophoneHardware", "USB\\B": 42, "USB\\C": "Microphone" }
            }
            """);

        DeviceLockJournal journal = new FileDeviceJournalStore(path).Load()!;

        // A restore line must only ever carry a kind the tray app knows, or it refuses the whole answer.
        Assert.Equal(DeviceKind.MicrophoneHardware, journal.KindOf(@"USB\A"));
        Assert.Equal(DeviceKind.Camera, journal.KindOf(@"USB\B"));
        Assert.Equal(DeviceKind.Camera, journal.KindOf(@"USB\C"));
        Assert.Single(journal.DeviceKinds);
    }

    [Fact]
    public void PersistentDisablesRoundTripAndDamagedOnesAreDropped()
    {
        string path = _temp.File("device-lock-journal.json");
        var store = new FileDeviceJournalStore(path);
        DeviceLockJournal journal = SampleJournal();
        journal.PersistentDisables.Add(journal.Actions[0].TargetId);
        store.Save(journal);

        Assert.True(store.Load()!.IsPersistent(journal.Actions[0].TargetId.ToLowerInvariant()));

        File.WriteAllText(path, """{ "phase": "Locked", "persistentDisables": null }""");
        Assert.Empty(store.Load()!.PersistentDisables);
        File.WriteAllText(path, """{ "phase": "Locked", "persistentDisables": [ "", "USB\\A" ] }""");
        Assert.Equal(["USB\\A"], store.Load()!.PersistentDisables);
    }

    [Fact]
    public void OwedRestoresLiveNextToTheJournalAndOutliveItsDeletion()
    {
        string path = _temp.File("device-lock-journal.json");
        var store = new FileDeviceJournalStore(path);
        Assert.Empty(store.LoadOwedRestores());
        store.Save(SampleJournal());
        OwedDevice owed = new() { InstanceId = @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000", Name = "USB Webcam", Kind = DeviceKind.Camera, Persistent = true };

        store.SaveOwedRestores([owed]);
        store.Delete();

        Assert.Equal(owed, Assert.Single(store.LoadOwedRestores()));
        Assert.True(File.Exists(_temp.File(FileDeviceJournalStore.OwedFileName)));
        Assert.Contains("\"kind\": \"Camera\"", File.ReadAllText(_temp.File(FileDeviceJournalStore.OwedFileName)), StringComparison.Ordinal);

        store.SaveOwedRestores([]);

        Assert.Empty(store.LoadOwedRestores());
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void DamagedOwedRestoresAreDroppedOrMadeSafe()
    {
        string owedPath = _temp.File(FileDeviceJournalStore.OwedFileName);
        var store = new FileDeviceJournalStore(_temp.File("device-lock-journal.json"));
        File.WriteAllText(
            owedPath,
            """
            [
              null,
              { "instanceId": "" },
              { "instanceId": "USB\\A", "kind": "Microphone" },
              { "instanceId": "USB\\B", "name": "Mic", "kind": "MicrophoneHardware" },
            ]
            """);

        IReadOnlyList<OwedDevice> owed = store.LoadOwedRestores();

        Assert.Equal(
            [
                new OwedDevice { InstanceId = @"USB\A", Name = @"USB\A", Kind = DeviceKind.Camera },
                new OwedDevice { InstanceId = @"USB\B", Name = "Mic", Kind = DeviceKind.MicrophoneHardware },
            ],
            owed);

        File.WriteAllText(owedPath, "not json");
        Assert.Empty(store.LoadOwedRestores());
    }

    [Fact]
    public void TheMemoryStoreRoundTripsThroughJsonLikeTheFile()
    {
        var store = new MemoryDeviceJournalStore();
        DeviceLockJournal journal = SampleJournal();

        store.Save(journal);
        journal.Actions.Clear(); // Later changes to the object must not leak into what was saved.
        DeviceLockJournal loaded = store.Load()!;

        Assert.Single(loaded.Actions);
        Assert.Equal(1, store.SaveCount);
        store.Corrupt = true;
        Assert.Throws<JournalCorruptException>(store.Load);
        store.Corrupt = false;
        store.Delete();
        Assert.Null(store.Load());
    }

    private static DeviceLockJournal SampleJournal() => new()
    {
        LockId = Guid.NewGuid(),
        Phase = LockPhase.Locked,
        StartedAt = new DateTimeOffset(2026, 10, 17, 21, 30, 0, TimeSpan.Zero),
        Request = new DeviceLockRequest
        {
            Cameras = true,
            MicHardware = true,
            IncludeInfrared = true,
            Exclusions = [new DeviceExclusion { StableKey = "cam|USB\\VID_046D&PID_085E&MI_00|LOGITECH BRIO", LastKnownId = "USB\\VID_046D&PID_085E&MI_00\\7&1", DisplayName = "Logitech BRIO" }],
            KeepEnabledInstanceIds = [@"USB\VID_2345&PID_6789&MI_00\8&2B3C"],
        },
        Actions =
        [
            new LockAction
            {
                Kind = ActionKind.DisableDevice,
                TargetId = @"USB\VID_3456&PID_789A&MI_00\6&4E5F6A7B&0&0000",
                DisplayName = "HD Webcam C270",
                WasAlreadyLocked = true,
                Status = ActionStatus.ApplyFailed,
                Note = "In use by an app; BlindsDown keeps trying.",
                GuardCount = 2,
                AddedWhileLocked = true,
            },
        ],
        DeviceKinds = new()
        {
            [@"USB\VID_3456&PID_789A&MI_00\6&4E5F6A7B&0&0000"] = DeviceKind.Camera,
            [@"USB\VID_3456&PID_789A&MI_02\6&4E5F6A7B&0&0002"] = DeviceKind.MicrophoneHardware,
        },
    };
}
