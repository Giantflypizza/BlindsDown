using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;

namespace BlindsDown.Tests.Core.Fakes;

/// <summary>
/// Everything a <see cref="LockEngine"/> runs against: fake devices, a fake clock, persisted stores
/// and settings. <see cref="Restart"/> simulates the process dying and starting again: a brand-new
/// engine over whatever the stores persisted, with the same devices.
/// </summary>
internal sealed class LockWorld
{
    public LockWorld()
    {
        Journal = MemoryJournal;
        Mics.Crash = Crash;
        Engine = CreateEngine();
    }

    public FakeClock Clock { get; } = new();

    public FakeMicBackend Mics { get; } = new();

    /// <summary>The default journal store. Round-trips through JSON on every save, like the real file.</summary>
    public MemoryJournalStore MemoryJournal { get; } = new();

    /// <summary>What survives a crash: the persisted journal.</summary>
    public IJournalStore Journal { get; private set; }

    /// <summary>What survives a crash: the persisted pending restores.</summary>
    public IPendingRestoreStore Pending { get; private set; } = new MemoryPendingRestoreStore();

    public CrashPlan Crash { get; } = new();

    public AppSettings Settings { get; set; } = new();

    public List<EngineAlert> Alerts { get; } = [];

    public List<LockStatusInfo> Statuses { get; } = [];

    public LockEngine Engine { get; private set; }

    /// <summary>The journal as persisted right now (null when there is none).</summary>
    public LockJournal? PersistedJournal => Journal.Load();

    /// <summary>Persists to other stores (e.g. real files) and starts a fresh engine over them.</summary>
    public void UseStores(IJournalStore journal, IPendingRestoreStore pending)
    {
        Journal = journal;
        Pending = pending;
        Engine = CreateEngine();
    }

    public LockEngine CreateEngine()
    {
        var engine = new LockEngine(
            Mics,
            new CrashingJournalStore(Journal, Crash),
            new CrashingPendingRestoreStore(Pending, Crash),
            Clock,
            () => Settings);
        engine.AlertRaised += Alerts.Add;
        engine.StatusChanged += Statuses.Add;
        return engine;
    }

    /// <summary>
    /// The process died: notifications in flight are lost, a new process starts and initializes.
    /// <paramref name="crashAt"/> &gt; 0 arms a crash for the new process too (from Initialize on).
    /// </summary>
    public StartupRecovery? Restart(int crashAt = 0)
    {
        Crash.Disarm();
        Mics.DropNotifications();
        Engine = CreateEngine();
        if (crashAt > 0)
        {
            Crash.ArmAt(crashAt);
        }

        return Engine.Initialize();
    }

    /// <summary>Delivers every queued device notification to the engine, as the engine thread would.</summary>
    public void Pump()
    {
        int guard = 0;
        while (Mics.TryDequeue(out FakeNotification n))
        {
            if (++guard > 10_000)
            {
                throw new InvalidOperationException("Notification storm: the engine keeps generating notifications.");
            }

            if (n.IsMuteChange)
            {
                Engine.OnMuteChanged(n.EndpointId, n.Muted, n.CausedByUs);
            }
            else
            {
                Engine.OnEndpointChanged(n.EndpointId);
            }
        }
    }

    public void Exclude(string id, bool byStableKeyOnly = false)
    {
        MicEndpoint endpoint = Mics[id].ToEndpoint();
        Settings.ExcludedDevices.Add(new DeviceExclusion
        {
            StableKey = endpoint.StableKey,
            LastKnownId = byStableKeyOnly ? null : endpoint.Id,
            DisplayName = endpoint.Name,
        });
    }

    public List<EngineAlert> AlertsOf(EngineAlertKind kind) => Alerts.Where(a => a.Kind == kind).ToList();

    public DeviceLockInfo DeviceStatus(string id) =>
        Engine.Status.Devices.Single(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public LockAction Action(string id) =>
        PersistedJournal!.Actions.Single(a => string.Equals(a.TargetId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Seeds the pending-restore store as if an earlier lock ended while <paramref name="id"/> was away
    /// (BlindsDown muted it and still owes it an unmute).</summary>
    public void SeedPendingRestore(string id) =>
        Pending.Save(
        [
            .. Pending.Load(),
            new LockAction
            {
                Kind = ActionKind.MuteMic,
                TargetId = id,
                DisplayName = Mics[id].Name,
                WasAlreadyLocked = false,
                Status = ActionStatus.RevertPending,
            },
        ]);
}
