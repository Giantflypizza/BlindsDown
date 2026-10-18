using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>
/// Everything a <see cref="DeviceLockEngine"/> runs against: fake devices, a fake clock and a persisted journal.
/// <see cref="Restart"/> simulates the helper dying and starting again (a brand-new engine over whatever the
/// journal persisted, same devices); <see cref="Reboot"/> also restarts the PC (every non-persistent disable is
/// gone).
/// </summary>
internal sealed class PnpWorld
{
    public PnpWorld()
    {
        Journal = MemoryJournal;
        Devices.Crash = Crash;
        Engine = CreateEngine();
    }

    /// <summary>The default request: colour cameras and microphone hardware.</summary>
    public static DeviceLockRequest Everything { get; } = new() { Cameras = true, MicHardware = true };

    public static DeviceLockRequest CamerasOnly { get; } = new() { Cameras = true };

    public FakeClock Clock { get; } = new();

    public FakePnpBackend Devices { get; } = new();

    /// <summary>Round-trips through JSON on every save, like the real file.</summary>
    public MemoryDeviceJournalStore MemoryJournal { get; } = new();

    /// <summary>What survives a crash.</summary>
    public IDeviceJournalStore Journal { get; private set; }

    public CrashPlan Crash { get; } = new();

    public List<EngineAlert> Alerts { get; } = [];

    public List<DeviceLockStatus> Statuses { get; } = [];

    public DeviceLockEngine Engine { get; private set; }

    public DeviceLockJournal? PersistedJournal => Journal.Load();

    /// <summary>Persists to another store (e.g. a real file) and starts a fresh engine over it.</summary>
    public void UseJournal(IDeviceJournalStore journal)
    {
        Journal = journal;
        Engine = CreateEngine();
    }

    public DeviceLockEngine CreateEngine()
    {
        var engine = new DeviceLockEngine(Devices, new CrashingDeviceJournalStore(Journal, Crash), Clock);
        engine.AlertRaised += Alerts.Add;
        engine.StatusChanged += Statuses.Add;
        return engine;
    }

    /// <summary>
    /// The helper died: notifications in flight are lost, a new helper starts and initializes.
    /// <paramref name="crashAt"/> &gt; 0 arms a crash for the new helper too (from Initialize on).
    /// </summary>
    public bool Restart(int crashAt = 0)
    {
        Crash.Disarm();
        Devices.TakeNotifications();
        Engine = CreateEngine();
        if (crashAt > 0)
        {
            Crash.ArmAt(crashAt);
        }

        return Engine.Initialize();
    }

    /// <summary>The PC restarted: devices lose every non-persistent disable, then the helper service starts.</summary>
    public bool Reboot(int crashAt = 0)
    {
        Crash.Disarm();
        Devices.Reboot();
        return Restart(crashAt);
    }

    /// <summary>Delivers the queued device notifications, as the watcher would (debounced into one call each round).</summary>
    public void Pump()
    {
        int rounds = 0;
        while (Devices.TakeNotifications() > 0)
        {
            if (++rounds > 100)
            {
                throw new InvalidOperationException("Notification storm: the engine keeps changing devices.");
            }

            Engine.OnDevicesChanged();
        }
    }

    public string Id(string shortId) => Devices[shortId].InstanceId;

    public DeviceLockInfo DeviceStatus(string shortId) =>
        Engine.Status.Devices.Single(d => string.Equals(d.Id, Id(shortId), StringComparison.OrdinalIgnoreCase));

    public LockAction Action(string shortId) =>
        PersistedJournal!.Actions.Single(a => string.Equals(a.TargetId, Id(shortId), StringComparison.OrdinalIgnoreCase));

    public bool HasAction(string shortId) =>
        PersistedJournal?.Actions.Exists(a => string.Equals(a.TargetId, Id(shortId), StringComparison.OrdinalIgnoreCase)) == true;

    public List<EngineAlert> AlertsOf(EngineAlertKind kind) => Alerts.Where(a => a.Kind == kind).ToList();

    /// <summary>An exclusion as Settings makes it (stable key + the ID the device had).</summary>
    public DeviceExclusion ExclusionFor(string shortId, bool byStableKeyOnly = false)
    {
        PnpDevice device = Devices[shortId].ToPnpDevice();
        return new DeviceExclusion
        {
            StableKey = device.StableKey,
            LastKnownId = byStableKeyOnly ? null : device.InstanceId,
            DisplayName = device.Name,
        };
    }

    /// <summary>
    /// Builds a world with <paramref name="setup"/>, records the crash points <paramref name="operation"/> passes,
    /// and returns how many there are.
    /// </summary>
    public static int CountCrashPoints(Func<PnpWorld> setup, Action<PnpWorld> operation)
    {
        PnpWorld probe = setup();
        probe.Crash.Record();
        operation(probe);
        probe.Crash.Disarm();
        return probe.Crash.Seen.Count;
    }
}

/// <summary>Journal store that can be "killed" before or after any write. The inner store is what survives the crash.</summary>
internal sealed class CrashingDeviceJournalStore(IDeviceJournalStore inner, CrashPlan crash) : IDeviceJournalStore
{
    public DeviceLockJournal? Load() => inner.Load();

    public void Save(DeviceLockJournal journal)
    {
        crash.Point($"before journal save ({journal.Phase})");
        inner.Save(journal);
        crash.Point($"after journal save ({journal.Phase})");
    }

    public void Delete()
    {
        crash.Point("before journal delete");
        inner.Delete();
        crash.Point("after journal delete");
    }

    public IReadOnlyList<OwedDevice> LoadOwedRestores() => inner.LoadOwedRestores();

    public void SaveOwedRestores(IReadOnlyList<OwedDevice> owed)
    {
        crash.Point("before owed save");
        inner.SaveOwedRestores(owed);
        crash.Point("after owed save");
    }
}

/// <summary>A journal store whose writes fail like a full or read-only disk (from the <see cref="FailAfter"/>-th save on).</summary>
internal sealed class FailingDeviceJournalStore : IDeviceJournalStore
{
    private readonly MemoryDeviceJournalStore _inner = new();
    private int _saves;

    /// <summary>Saves that still work before the disk "fills up". 0: every save fails.</summary>
    public int FailAfter { get; set; }

    public bool FailDeletes { get; set; }

    public DeviceLockJournal? Load() => _inner.Load();

    public void Save(DeviceLockJournal journal)
    {
        if (++_saves > FailAfter)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        _inner.Save(journal);
    }

    public void Delete()
    {
        if (FailDeletes)
        {
            throw new UnauthorizedAccessException("Access to the path is denied.");
        }

        _inner.Delete();
    }

    /// <summary>The owed restores' record fails too, while <see cref="FailOwedSaves"/>.</summary>
    public bool FailOwedSaves { get; set; }

    public IReadOnlyList<OwedDevice> LoadOwedRestores() => _inner.LoadOwedRestores();

    public void SaveOwedRestores(IReadOnlyList<OwedDevice> owed)
    {
        if (FailOwedSaves)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        _inner.SaveOwedRestores(owed);
    }
}
