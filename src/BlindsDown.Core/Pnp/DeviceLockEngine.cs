using System.Runtime.InteropServices;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Time;

namespace BlindsDown.Core.Pnp;

/// <summary>
/// The helper service's lock: disables cameras and (when asked) microphone-only hardware at the device level,
/// guards them while locked, and re-enables exactly what it disabled. Same rules as <see cref="LockEngine"/>:
///  * Only devices that are present, working (not already disabled) and not excluded are touched; devices the
///    user disabled stay disabled. Infrared and virtual cameras only when the request includes them;
///    <see cref="DeviceLockRequest.KeepEnabledInstanceIds"/> are never touched.
///  * Journal first (write-ahead, "Applying" before the change), then change, then verify against Windows' own
///    device list.
///  * Unlock re-enables only what this engine may have disabled, and only if it is still disabled.
///  * A vetoed disable (device in use) is not a lock: the device is reported as a problem ("In use by an app;
///    BlindsDown keeps trying") with a <see cref="EngineAlertKind.DeviceBusy"/> alert, and retried on every
///    <see cref="Reconcile"/>.
///  * After a restart devices come back enabled (the disable is not persistent, so a removed or broken
///    BlindsDown can never leave a camera off for good); <see cref="Initialize"/> re-applies a lock that is still
///    in the journal. A device unplugged while locked also comes back enabled, so its return is guarded too. Should
///    Windows make a disable persistent anyway (<see cref="PnpChange.MadePersistent"/>), the journal records it, so
///    BlindsDown can still turn that device back on after a restart.
///  * The engine only ever disables devices the backend lists as cameras or microphone hardware. The journal
///    can only make it turn devices back ON (so a device BlindsDown turned off is never left off just because
///    Windows stopped listing it).
///  * A request can only make it do less, but never quietly: a device turned back on because a changed request no
///    longer covers it, or one the lock was still turning off (in use, unplugged) and no longer does, raises an urgent
///    <see cref="EngineAlertKind.ReEnabledWhileLocked"/> alert naming it.
///  * A device an unlock could not turn back on is retried while the helper runs, and remembered across restarts of
///    the helper (<see cref="IDeviceJournalStore.LoadOwedRestores"/>), so it is never taken for one the user turned off.
/// Not thread-safe: called from one thread.
/// </summary>
public sealed class DeviceLockEngine
{
    internal const string InUseProblem = "In use by an app; BlindsDown keeps trying.";

    /// <summary>How the alert about a lock request that left devices out starts.</summary>
    internal const string NarrowedAlertPrefix = "A change to the lock left";
    internal const string CorruptJournalProblem = "BlindsDown's record of the camera lock could not be read. Restart the PC to turn every camera back on.";
    internal const string ListFailedProblem = "Windows could not list the cameras; BlindsDown keeps trying.";
    internal const string NotSavedProblem = "BlindsDown can't save its lock record, so it didn't turn it off.";
    internal const string NotYetProblem = "Not turned off yet; BlindsDown keeps trying.";
    internal const string OwedRestoreProblem = "BlindsDown couldn't turn it back on and keeps trying. Restarting the PC also turns it back on.";
    internal const string OwedPersistentProblem = "BlindsDown couldn't turn it back on and keeps trying. Windows keeps it off even after a restart; Device Manager can turn it back on.";
    internal const string KeptForMicrophoneProblem = "Left on: it is also a microphone BlindsDown has to keep on (a default or excluded microphone).";
    internal const string NotConnectedLine = "Not connected right now; it will work when plugged back in.";
    internal const string KeptOffLine = "Turned off for good in Windows (in Device Manager or Settings, or by an administrator), so BlindsDown left it off.";
    internal const string PersistentAwayLine = "Not connected right now. Windows keeps it off even when it is plugged back in, so BlindsDown turns it back on then.";
    internal const string RestartAdvice = "Restart the PC to turn it back on.";
    internal const string DeviceManagerAdvice = "Windows keeps it off even after a restart: turn it back on in Device Manager.";

    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TamperWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A device that is in use is retried on every <see cref="Reconcile"/> (the app may let go any second). Other
    /// failures (needs a restart, access denied, driver errors) rarely go away by themselves, and each attempt can
    /// briefly stop and restart the device, so they are retried at most this often.
    /// </summary>
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RestoreRetryInterval = TimeSpan.FromSeconds(30);
    private const int TamperThreshold = 5;
    private const int MaxDetailLength = 160;
    private const string LockRecord = "lock record";
    private const string LockRecordCleanup = "lock record cleanup";

    private readonly IPnpBackend _backend;
    private readonly IDeviceJournalStore _journalStore;
    private readonly IClock _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _guardTimes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last failed disable per device in this lock, for the retry interval.</summary>
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"device|result" pairs already alerted in this lock: one alert per device and kind of failure.</summary>
    private readonly HashSet<string> _failureAlerts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Locked devices seen unplugged, so their return can be told apart from someone turning them on.</summary>
    private readonly HashSet<string> _away = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Devices an unlock could not turn back on, retried while the helper runs and saved for its next start
    /// (a restart of the PC also fixes them, unless Windows made the disable persistent).</summary>
    private readonly List<OwedRestore> _owed = [];

    /// <summary>
    /// Devices in the lock that Windows didn't list when the request changed, so the new request couldn't be matched
    /// against them, with what their release will announce. Checked when they are listed again, before the guard may
    /// turn them off: otherwise a device the app now needs on (<see cref="DeviceLockRequest.KeepEnabledInstanceIds"/>)
    /// would be turned off on its return.
    /// </summary>
    private readonly Dictionary<string, ReleaseNews> _releaseWhenListed = new(StringComparer.OrdinalIgnoreCase);

    private DeviceLockJournal? _journal;
    private IReadOnlyList<PnpDevice> _devices = [];
    private bool _initialized;
    private bool _listFailed;

    /// <summary>The next pass that can list the devices is part of a lock being (re)applied, not a change while locked.</summary>
    private bool _quietPassPending;

    /// <summary>The request changed (or the helper just started): the next pass that can list the devices releases
    /// the ones the request no longer covers.</summary>
    private bool _releasePending;

    /// <summary>What the pending release announces (see <see cref="ReleaseNews"/>).</summary>
    private ReleaseNews _releaseNews = ReleaseNews.Everything;

    /// <summary>The owed restores changed but could not be saved; saving is retried on every pass.</summary>
    private bool _owedNotSaved;

    /// <summary>Why the last lock could not even start (its record could not be written).</summary>
    private string? _startProblem;

    /// <summary>
    /// An unlock finished but its lock record could not be deleted. Deleting is retried on every pass until it works:
    /// if the record's last saves failed too, it still says "locked", and the next start would turn the devices off
    /// again after the user unlocked.
    /// </summary>
    private bool _recordLeftOver;

    private string _lastStatusSignature = string.Empty;

    public DeviceLockEngine(IPnpBackend backend, IDeviceJournalStore journal, IClock clock)
    {
        _backend = backend;
        _journalStore = journal;
        _clock = clock;
    }

    public event Action<DeviceLockStatus>? StatusChanged;

    public event Action<EngineAlert>? AlertRaised;

    /// <summary>The current state with every device (see <see cref="DeviceLockStatus"/>).</summary>
    public DeviceLockStatus Status { get; private set; } = DeviceLockStatus.Unlocked;

    /// <summary>True while a lock exists (including one an interrupted lock or unlock left behind).</summary>
    public bool HasLock => _journal is not null;

    /// <summary>Set when the journal file exists but could not be read. Nothing was changed because of it.</summary>
    public bool JournalWasCorrupt { get; private set; }

    /// <summary>At service start: reads the device list, loads the journal and, if a lock is active, re-applies
    /// it. Returns true when a lock is active.</summary>
    public bool Initialize()
    {
        _initialized = true;
        LoadOwed();
        if (!LoadJournal())
        {
            // Devices an unlock before the restart could not turn back on are due now.
            if (RefreshDevices())
            {
                RetryOwedRestores();
            }

            PublishStatus();
            return false;
        }

        if (_journal!.Phase == LockPhase.Unlocking)
        {
            // An unlock was interrupted: finish it. After a restart there is usually nothing left to turn on.
            Unlock();
            return false;
        }

        // After a restart every device this lock turned off is on again (the disable is not persistent), and after
        // a crash some may not have been reached yet: re-apply, without "turned back on" alerts, since that is expected.
        // A change of request that a crash interrupted is finished too (the record says "locking" until a change is
        // done), and announced like one. Otherwise only a device that pass actually turns back on is news: after a
        // restart of the PC every device is on anyway, and one that only stopped matching the same request (Windows
        // describes it differently now) was not left out by a change.
        _quietPassPending = true;
        _releasePending = true;
        _releaseNews = _journal.Phase == LockPhase.Locking ? ReleaseNews.Everything : ReleaseNews.TurnedOnOnly;
        Enforce(announce: false, retryFailures: true);
        SetPhase(LockPhase.Locked);
        PublishStatus();
        return true;
    }

    /// <summary>Locks the requested devices. If already locked with a different request, the lock is widened
    /// or narrowed to match (devices no longer requested are re-enabled, with an urgent alert). The same request
    /// again only re-checks every device.</summary>
    public DeviceLockStatus Lock(DeviceLockRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_initialized)
        {
            // A lock still on disk must be taken over, never started over: a new journal would record the devices
            // the old lock turned off as "already off", and they would stay off after the unlock.
            Initialize();
        }

        DeviceLockRequest wanted = request.Normalized();
        if (_journal is null)
        {
            StartLock(wanted);
        }
        else if (_journal.Phase == LockPhase.Locked && _journal.Request.LocksSameAs(wanted))
        {
            Enforce(announce: true, retryFailures: true);
        }
        else
        {
            ChangeLock(wanted);
        }

        PublishStatus();
        return Status;
    }

    /// <summary>
    /// Re-enables everything this lock disabled. When not locked, tries the devices an earlier unlock could not turn
    /// back on right away and reports them (the uninstaller and --restore rely on that); returns null when there is
    /// neither a lock nor such a device.
    /// </summary>
    public RestoreReport? Unlock()
    {
        if (!_initialized)
        {
            // Unlocking needs the record, not a re-applied lock.
            _initialized = true;
            LoadOwed();
            LoadJournal();
        }

        _startProblem = null;
        DeviceLockJournal? journal = _journal;
        if (journal is null)
        {
            RestoreReport? owedReport = RestoreOwedNow();
            PublishStatus();
            return owedReport;
        }

        journal.Phase = LockPhase.Unlocking;
        TrySave();
        PublishStatus();

        bool listed = RefreshDevices();
        var lines = new List<RestoreLine>();
        for (int i = journal.Actions.Count - 1; i >= 0; i--)
        {
            LockAction action = journal.Actions[i];
            ActionState before = Snapshot(action);
            lines.Add(Restore(journal, action, listed));
            SaveIfChanged(action, before);
        }

        lines.Reverse();
        bool owedAdded = false;
        foreach (LockAction failed in journal.Actions.Where(a => a.Status == ActionStatus.RevertFailed))
        {
            // One that is away (only a persistent disable is owed then) is tried as soon as it is back.
            bool away = Find(failed.TargetId) is { IsPresent: false };
            DateTimeOffset nextTry = away ? _clock.UtcNow : _clock.UtcNow + RestoreRetryInterval;
            owedAdded |= Owe(failed.TargetId, failed.DisplayName, journal.KindOf(failed.TargetId), journal.IsPersistent(failed.TargetId), nextTry);
        }

        // Saved before the record is deleted: in between, a crash leaves the record, and the next start finishes
        // this unlock.
        if (owedAdded)
        {
            SaveOwed();
        }

        if (TryDeleteRecord() is { } error)
        {
            // Only the file is left. Deleting it is retried on every pass; if the helper starts again first, the
            // record says "unlocking" (when its saves worked) and the start just tidies up.
            Alert(EngineAlertKind.LockFailed, LockRecordCleanup, $"The camera lock is off, but BlindsDown couldn't delete its lock record ({error}).", urgent: false);
        }

        _journal = null;
        ForgetLockMemory();
        PublishStatus();
        return new RestoreReport(lines, journal.Actions.Count, CoversCameras: true);
    }

    /// <summary>Devices were added, removed, enabled or disabled: refresh the list, lock new ones, re-disable
    /// re-enabled ones (alert), finish deferred restores. Failed disables wait for <see cref="Reconcile"/>, so a
    /// device that stops and restarts on every attempt can't start a storm of notifications.</summary>
    public void OnDevicesChanged() => Refresh(retryFailures: false);

    /// <summary>Safety net, every few seconds: retries vetoed disables and catches missed notifications.</summary>
    public void Reconcile() => Refresh(retryFailures: true);

    /// <summary>Whether <paramref name="device"/> is covered by <paramref name="request"/>, given every device
    /// listed right now (exclusions are matched against the connected devices of the same kind).</summary>
    internal static bool InScope(PnpDevice device, DeviceLockRequest request, IReadOnlyList<PnpDevice> all) =>
        IsRequested(device, request)
        && !IsKeptOn(device, request)
        && !IsExcluded(device, request, all);

    /// <summary>
    /// The app needs this device left on, because it is the hardware of a default or excluded microphone. For a
    /// camera that is only possible when one device is both (a capture card, a single-function webcam): turning it off
    /// would take that microphone away and let Windows move apps to another one, so it stays on, as a problem.
    /// </summary>
    private static bool IsKeptOn(PnpDevice device, DeviceLockRequest request) =>
        request.KeepEnabledInstanceIds.Any(id => SameId(id, device.InstanceId));

    internal static bool IsExcluded(PnpDevice device, DeviceLockRequest request, IReadOnlyList<PnpDevice> all) =>
        request.Exclusions.Count > 0
        && ExclusionRules.IsExcluded(
            request.Exclusions.Select(e => (e.StableKey, e.LastKnownId)),
            device.InstanceId,
            device.StableKey,
            all.Where(d => d.Kind == device.Kind && d.IsPresent).Select(d => (d.InstanceId, d.StableKey)).ToList());

    /// <summary>
    /// A camera's type for the lock. A software device counts as virtual whatever else it reports, and a missing
    /// or unknown type as colour: locked by default, which a wrong guess only costs Windows Hello, never privacy.
    /// </summary>
    private static CameraType CameraTypeOf(PnpDevice device) =>
        device.IsVirtual ? CameraType.Virtual
        : device.CameraType is { } type && Enum.IsDefined(type) ? type
        : CameraType.Color;

    private static bool IsRequested(PnpDevice device, DeviceLockRequest request) => device.Kind switch
    {
        DeviceKind.Camera => request.Cameras && CameraTypeOf(device) switch
        {
            CameraType.Infrared => request.IncludeInfrared,
            CameraType.Virtual => request.IncludeVirtual,
            _ => true,
        },

        // Virtual microphones are only ever muted by the app: they go silent when the physical ones are off.
        DeviceKind.MicrophoneHardware => request.MicHardware && !device.IsVirtual,
        _ => false,
    };

    private void Refresh(bool retryFailures)
    {
        if (!_initialized)
        {
            Initialize();
            return;
        }

        if (_owedNotSaved)
        {
            SaveOwed();
        }

        if (_journal is null)
        {
            if (_recordLeftOver)
            {
                TryDeleteRecord();
            }

            if (RefreshDevices())
            {
                RetryOwedRestores();
            }
        }
        else if (_journal.Phase == LockPhase.Unlocking)
        {
            RefreshDevices(); // An unlock that stopped half-way; the next Unlock finishes it.
        }
        else
        {
            Enforce(announce: true, retryFailures);
        }

        PublishStatus();
    }

    private bool LoadJournal()
    {
        DeviceLockJournal? journal;
        try
        {
            journal = _journalStore.Load();
        }
        catch (JournalCorruptException)
        {
            // Nothing is changed: without the record BlindsDown can't tell which devices it turned off. A restart
            // turns every one of them back on (the disable is not persistent), and the status says so.
            JournalWasCorrupt = true;
            return false;
        }

        if (journal is null)
        {
            return false;
        }

        journal.Repair();
        _journal = journal;
        return true;
    }

    private void StartLock(DeviceLockRequest request)
    {
        ForgetLockMemory();
        bool listed = RefreshDevices();
        var journal = new DeviceLockJournal
        {
            LockId = Guid.NewGuid(),
            Phase = LockPhase.Locking,
            StartedAt = _clock.UtcNow,
            Request = request,
        };

        // Without a fresh list the lock starts empty; the first pass that can list the devices adds them.
        foreach (PnpDevice device in listed ? _devices.Where(d => d.IsPresent && InScope(d, request, _devices)) : [])
        {
            AddAction(journal, device, addedWhileLocked: false);
        }

        // Journal first: if it can't be written, nothing has been changed and nothing is locked.
        try
        {
            _journalStore.Save(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _startProblem = $"BlindsDown can't save its lock record ({ex.Message}), so it didn't turn anything off.";
            Alert(EngineAlertKind.LockFailed, LockRecord, _startProblem, urgent: true);
            return;
        }

        _startProblem = null;
        _recordLeftOver = false; // The new record replaced it.
        _journal = journal;
        if (_owed.RemoveAll(o => FindAction(o.InstanceId) is not null) > 0)
        {
            SaveOwed(); // This lock's record has them now (and its unlock turns them back on).
        }

        _quietPassPending = !listed;
        PublishStatus();

        Enforce(announce: false, retryFailures: true);
        SetPhase(LockPhase.Locked);
    }

    /// <summary>A different request while locked (or a lock or unlock that was interrupted): re-enable what the new
    /// request no longer covers, then lock what it newly covers.</summary>
    private void ChangeLock(DeviceLockRequest request)
    {
        DeviceLockJournal journal = _journal!;

        // After an interrupted unlock, those devices were being turned back on anyway: that is not news.
        _releaseNews = journal.Phase == LockPhase.Unlocking ? ReleaseNews.Nothing : ReleaseNews.Everything;
        if (journal.Phase == LockPhase.Unlocking)
        {
            // Locking again after an interrupted unlock: devices it turned back on are untouched again; devices it
            // could not turn on are still off because of BlindsDown.
            foreach (LockAction action in journal.Actions)
            {
                action.Status = action.Status switch
                {
                    ActionStatus.Reverted => ActionStatus.Planned,
                    ActionStatus.RevertFailed or ActionStatus.RevertPending => ActionStatus.Applying,
                    _ => action.Status,
                };
            }
        }

        // The new request is recorded first: a crash from here on re-applies the lock with the devices it still
        // lists (finishing the release at start), and unlock turns all of them back on.
        journal.Phase = LockPhase.Locking;
        journal.Request = request;
        TrySave();

        _releasePending = true;
        Enforce(announce: false, retryFailures: true);
        SetPhase(LockPhase.Locked);
    }

    /// <summary>
    /// Re-enables and drops the devices the lock's request no longer covers: every device in the lock, or with
    /// <paramref name="onlyUnlisted"/> only those that Windows didn't list at the last check (see
    /// <see cref="_releaseWhenListed"/>). A device Windows doesn't list can't be matched against the request (its
    /// kind of camera and its stable key are unknown), so it keeps its place until it is listed again.
    /// What this turns back on, or stops turning off (a device in use or unplugged), is announced as
    /// <paramref name="news"/> allows: only a request can make the lock cover less, the app never sends a narrower one
    /// while locked, and any program can send one.
    /// </summary>
    private void ReleaseUnrequested(DeviceLockJournal journal, bool onlyUnlisted, ReleaseNews news)
    {
        var turnedOn = new List<(string Name, DeviceKind Kind)>();
        var stopped = new List<(string Name, DeviceKind Kind)>();
        foreach (LockAction action in journal.Actions.ToList())
        {
            ReleaseNews deferred = ReleaseNews.Nothing;
            if (onlyUnlisted && !_releaseWhenListed.TryGetValue(action.TargetId, out deferred))
            {
                continue;
            }

            ReleaseNews told = onlyUnlisted ? deferred : news;
            PnpDevice? device = Find(action.TargetId);
            if (device is null)
            {
                // Told once it is listed, as this pass would have (the more of the two, should it wait twice).
                _releaseWhenListed[action.TargetId] = _releaseWhenListed.TryGetValue(action.TargetId, out ReleaseNews earlier) && earlier > told ? earlier : told;
                continue;
            }

            _releaseWhenListed.Remove(action.TargetId);
            if (InScope(device, journal.Request, _devices) || !Release(action, device, out Unguarded unguarded))
            {
                continue;
            }

            if (unguarded == Unguarded.TurnedOn && told >= ReleaseNews.TurnedOnOnly)
            {
                turnedOn.Add((action.DisplayName, device.Kind));
            }
            else if (unguarded == Unguarded.NoLongerTurnedOff && told == ReleaseNews.Everything)
            {
                stopped.Add((action.DisplayName, device.Kind));
            }

            Drop(journal, action);
        }

        AnnounceReleased(turnedOn, stopped);
    }

    /// <summary>
    /// Takes a device out of the lock, turning it back on if this lock may have turned it off. Returns false when it
    /// could not be turned on: it then stays in the lock (still off, still guarded) and unlock tries again.
    /// <paramref name="unguarded"/> says what the release changed for the device.
    /// </summary>
    private bool Release(LockAction action, PnpDevice device, out Unguarded unguarded)
    {
        unguarded = Unguarded.Nothing;
        if (action.WasAlreadyLocked)
        {
            return true; // Off before the lock: the user's, and left as it is.
        }

        if (!MayBeOurs(action.Status) || !device.IsDisabled)
        {
            // On, in use or unplugged (back on when plugged in): the lock was still turning it off, and now won't.
            unguarded = device.IsDisabled ? Unguarded.Nothing : Unguarded.NoLongerTurnedOff;
            return true;
        }

        bool persistent = _journal!.IsPersistent(action.TargetId);
        if (!device.IsPresent)
        {
            // Away: it comes back on by itself, unless Windows made the disable persistent. Then it is owed, and
            // turned on as soon as it is back.
            if (persistent && Owe(action.TargetId, action.DisplayName, device.Kind, persistent, _clock.UtcNow))
            {
                SaveOwed();
            }

            unguarded = persistent ? Unguarded.NoLongerTurnedOff : Unguarded.Nothing;
            return true;
        }

        if (TurnOn(action.TargetId, persistent, out PnpChange change, out bool away))
        {
            unguarded = away ? Unguarded.NoLongerTurnedOff : Unguarded.TurnedOn;
            return true;
        }

        if (change.Result == PnpChangeResult.KeptOff)
        {
            return true; // Turned off for good by someone else since: theirs now, and left off.
        }

        Alert(
            EngineAlertKind.LockFailed,
            action.DisplayName,
            $"BlindsDown couldn't turn a {Noun(device.Kind)} back on ({TurnOnProblem(change)}). It stays off until you unlock: {action.DisplayName}.",
            urgent: true);
        return false;
    }

    private void Drop(DeviceLockJournal journal, LockAction action)
    {
        journal.Actions.Remove(action);
        if (!journal.Actions.Exists(a => SameId(a.TargetId, action.TargetId)))
        {
            foreach (string key in journal.DeviceKinds.Keys.Where(k => SameId(k, action.TargetId)).ToList())
            {
                journal.DeviceKinds.Remove(key);
            }

            journal.PersistentDisables.RemoveAll(id => SameId(id, action.TargetId));
        }

        _away.Remove(action.TargetId);
        _failures.Remove(action.TargetId);
        _releaseWhenListed.Remove(action.TargetId);
        TrySave();
    }

    /// <summary>
    /// Makes the device list match the lock: every journaled device off (re-disabling what came back on), every
    /// newly connected device in scope added and turned off. <paramref name="announce"/> is false while a lock
    /// is being applied or re-applied, when devices being on is expected rather than news.
    /// </summary>
    private void Enforce(bool announce, bool retryFailures)
    {
        DeviceLockJournal journal = _journal!;
        if (!RefreshDevices())
        {
            return; // Nothing can be checked; the status says so and the safety poll keeps trying.
        }

        // Once a device is in the lock it stays in until the request changes, even if it stops matching (an
        // exclusion that now fits a twin, a camera Windows describes differently while it is off): re-evaluating it
        // on every pass could turn a device on and off in a loop. A device Windows didn't list when the request
        // changed is matched once, when it is listed again.
        if (_releasePending || _releaseWhenListed.Count > 0)
        {
            bool onlyUnlisted = !_releasePending;
            ReleaseNews news = _releaseNews;
            _releasePending = false;
            _releaseNews = ReleaseNews.Everything;
            ReleaseUnrequested(journal, onlyUnlisted, news);
        }

        announce &= !_quietPassPending;
        _quietPassPending = false;
        foreach (LockAction action in journal.Actions.ToList())
        {
            Guard(action, Find(action.TargetId), announce, retryFailures);
        }

        foreach (PnpDevice listed in _devices.ToList())
        {
            // Looked up again: the list is refreshed after every change.
            PnpDevice? device = Find(listed.InstanceId);
            if (device is { IsPresent: true } && FindAction(device.InstanceId) is null && InScope(device, journal.Request, _devices))
            {
                AddWhileLocked(journal, device, announce);
            }
        }

        // A device an earlier unlock could not turn back on that this lock doesn't cover (it took over the others
        // above): the lock doesn't want it off either.
        RetryOwedRestores();
    }

    private void Guard(LockAction action, PnpDevice? device, bool announce, bool retryFailures)
    {
        if (device is not { IsPresent: true })
        {
            // Away. Whatever BlindsDown did to it, it comes back on (the disable is not persistent), so its return
            // is watched for.
            _away.Add(action.TargetId);
            return;
        }

        bool cameBack = _away.Remove(action.TargetId);
        if (device.IsDisabled)
        {
            MarkOff(action);
            return;
        }

        ActionStatus before = action.Status;
        if (before == ActionStatus.ApplyFailed && !cameBack && !(retryFailures && RetryDue(action.TargetId)))
        {
            return;
        }

        TurnOff(action, device);
        if (!announce || before != ActionStatus.Applied)
        {
            return;
        }

        if (cameBack)
        {
            AnnounceReturn(action, device);
        }
        else
        {
            AnnounceReEnabled(action, device);
        }
    }

    /// <summary>The device is off, as the lock wants it.</summary>
    private void MarkOff(LockAction action)
    {
        ActionState before = Snapshot(action);
        if (action.Status == ActionStatus.Planned)
        {
            // Untouched by this lock so far, so it was off before BlindsDown did anything: unlock leaves it off.
            action.WasAlreadyLocked = true;
        }

        action.Status = ActionStatus.Applied;
        action.Note = null;
        _failures.Remove(action.TargetId);
        SaveIfChanged(action, before);
    }

    private void TurnOff(LockAction action, PnpDevice device)
    {
        ActionState before = Snapshot(action);
        if (!MayBeOurs(action.Status))
        {
            // Write-ahead: once the journal says "Applying", a crash right after the change still gets the device
            // turned back on. If that record can't be written, the device is not touched.
            if (action.Status == ActionStatus.Planned)
            {
                action.WasAlreadyLocked = false; // Untouched so far and on right now, so "on" is what unlock restores.
            }

            action.Status = ActionStatus.Applying;
            if (!TrySave())
            {
                action.Status = before.Status;
                action.WasAlreadyLocked = before.WasAlreadyLocked;
                action.Note = NotSavedProblem;
                return;
            }
        }

        PnpChange change = Change(action.TargetId, disable: true);
        if (change.MadePersistent && !_journal!.IsPersistent(action.TargetId))
        {
            // Only an enable told so turns it back on, also after a restart: recorded right away.
            _journal.PersistentDisables.Add(action.TargetId);
            TrySave();
        }

        bool listed = RefreshDevices();
        PnpDevice? now = listed ? Find(action.TargetId) : null;
        if (change.Result == PnpChangeResult.NotFound || (listed && now is not { IsPresent: true }))
        {
            // It left while being turned off. Whatever happened, it comes back on, and its return is watched for.
            _away.Add(action.TargetId);
            SaveIfChanged(action, before);
            return;
        }

        // Verified against Windows' list. If the list can't be read, the backend's answer (which it verifies too) stands.
        if (now?.IsDisabled ?? change.Result == PnpChangeResult.Done)
        {
            action.Status = ActionStatus.Applied;
            action.Note = null;
            _failures.Remove(action.TargetId);
        }
        else
        {
            // Not off. It still counts as "may be off because of BlindsDown" (a refused change can still take effect
            // late), so unlock turns it back on if it is ever found off.
            action.Status = ActionStatus.ApplyFailed;
            action.Note = TurnOffProblem(change);
            _failures[action.TargetId] = new Failure(change.Result, _clock.UtcNow);
            AlertFailure(action, device.Kind, change.Result);
        }

        SaveIfChanged(action, before);
    }

    private void AddWhileLocked(DeviceLockJournal journal, PnpDevice device, bool announce)
    {
        LockAction action = AddAction(journal, device, addedWhileLocked: true);
        if (!TrySave())
        {
            // Not recorded, so it must not be touched: turning it off now could never be undone after a crash.
            journal.Actions.Remove(action);
            journal.DeviceKinds.Remove(device.InstanceId);
            journal.PersistentDisables.RemoveAll(id => SameId(id, device.InstanceId));
            return;
        }

        if (_owed.RemoveAll(o => SameId(o.InstanceId, device.InstanceId)) > 0)
        {
            SaveOwed();
        }

        Guard(action, device, announce: false, retryFailures: true);
        if (announce && action.Status == ActionStatus.Applied && !action.WasAlreadyLocked)
        {
            string noun = Noun(device.Kind);
            Alert(EngineAlertKind.NewDeviceLocked, device.Name, $"A new {noun} was connected while locked. BlindsDown turned it off: {device.Name}.", urgent: false);
        }
    }

    /// <summary>
    /// The journal entry for a device about to be locked. A device an earlier unlock could not turn back on is
    /// still off because of BlindsDown: it starts as "may already be off because of BlindsDown", so this lock's
    /// unlock turns it on instead of leaving it off as if the user had disabled it.
    /// </summary>
    private LockAction AddAction(DeviceLockJournal journal, PnpDevice device, bool addedWhileLocked)
    {
        OwedRestore? owed = FindOwed(device.InstanceId);
        var action = new LockAction
        {
            Kind = ActionKind.DisableDevice,
            TargetId = device.InstanceId,
            DisplayName = device.Name,
            WasAlreadyLocked = owed is null && device.IsDisabled,
            Status = owed is not null ? ActionStatus.Applying : ActionStatus.Planned,
            AddedWhileLocked = addedWhileLocked,
        };
        journal.Actions.Add(action);
        journal.DeviceKinds[device.InstanceId] = device.Kind;
        if (owed is { Persistent: true } && !journal.IsPersistent(device.InstanceId))
        {
            journal.PersistentDisables.Add(device.InstanceId);
        }

        return action;
    }

    private RestoreLine Restore(DeviceLockJournal journal, LockAction action, bool listed)
    {
        PnpDevice? device = listed ? Find(action.TargetId) : null;
        DeviceKind kind = device?.Kind ?? journal.KindOf(action.TargetId);
        RestoreLine Line(RestoreOutcome outcome, string message) => new(outcome, action.DisplayName, message, kind);

        if (action.WasAlreadyLocked)
        {
            // Off before the lock: left as it is.
            action.Status = ActionStatus.Reverted;
            action.Note = null;
            return device switch
            {
                null => Line(RestoreOutcome.Exact, "Left off, as before the lock."),
                { IsPresent: false } => Line(RestoreOutcome.Exact, "Not connected right now; it was off before the lock."),
                { IsDisabled: true } => Line(RestoreOutcome.Exact, "Off, as before the lock."),
                _ => Line(RestoreOutcome.ChangedByOthers, "Was off before the lock but is on now (changed by another app or by you)."),
            };
        }

        if (!MayBeOurs(action.Status))
        {
            // Never turned off by this lock, or already turned back on by an unlock that was interrupted.
            action.Status = ActionStatus.Reverted;
            action.Note = null;
            return device switch
            {
                null => Line(RestoreOutcome.Exact, "Nothing left to restore."),
                { IsPresent: false } => Line(RestoreOutcome.Exact, NotConnectedLine),
                { IsDisabled: true } => Line(RestoreOutcome.ChangedByOthers, "Was on before the lock but is off now (changed by another app or by you)."),
                _ => Line(RestoreOutcome.Exact, "On, as before the lock."),
            };
        }

        bool persistent = journal.IsPersistent(action.TargetId);
        if (persistent && device is { IsPresent: false, IsDisabled: true })
        {
            // Away, but a persistent disable comes back with it: owed until it is plugged back in.
            action.Status = ActionStatus.RevertFailed;
            action.Note = null;
            return Line(RestoreOutcome.Pending, PersistentAwayLine);
        }

        if (device is { IsPresent: false } || device is { IsDisabled: false })
        {
            // Away (it comes back on by itself), or already on.
            action.Status = ActionStatus.Reverted;
            action.Note = null;
            return Line(RestoreOutcome.Exact, device.IsPresent ? "On, as before the lock." : NotConnectedLine);
        }

        // Off, or not in Windows' list (then Windows is asked directly, so a device BlindsDown turned off is never
        // left off just because it can't be listed).
        if (TurnOn(action.TargetId, persistent, out PnpChange change, out bool away))
        {
            action.Status = ActionStatus.Reverted;
            action.Note = null;
            return Line(RestoreOutcome.Exact, away ? NotConnectedLine : "Turned back on, as before the lock.");
        }

        if (change.Result == PnpChangeResult.KeptOff)
        {
            // Someone turned it off for good during the lock (or before it, while it was still running): theirs.
            action.Status = ActionStatus.Reverted;
            action.Note = null;
            return Line(RestoreOutcome.ChangedByOthers, KeptOffLine);
        }

        action.Status = ActionStatus.RevertFailed;
        action.Note = TurnOnProblem(change);
        return Line(RestoreOutcome.Failed, $"Could not be turned back on ({action.Note}). {Advice(persistent)}");
    }

    /// <summary>
    /// Turns a device back on and checks the result. True when it is on, or away (it comes back on by itself; not a
    /// device whose disable Windows made persistent, <paramref name="persistent"/>). False with
    /// <see cref="PnpChangeResult.KeptOff"/> for a device someone else turned off for good.
    /// </summary>
    private bool TurnOn(string instanceId, bool persistent, out PnpChange change, out bool away)
    {
        change = Change(instanceId, disable: false, clearPersistent: persistent);
        PnpDevice? now = RefreshDevices() ? Find(instanceId) : null;
        away = change.Result == PnpChangeResult.NotFound || now is { IsPresent: false };
        bool on = now?.IsDisabled == false || (now is null && change.Result == PnpChangeResult.Done);
        return on || (away && !persistent);
    }

    /// <summary>Retries the owed restores that are due; drops the ones that need nothing any more.</summary>
    private void RetryOwedRestores()
    {
        bool changed = false;
        foreach (OwedRestore owed in _owed.ToList())
        {
            PnpDevice? device = Find(owed.InstanceId);
            if (FindAction(owed.InstanceId) is not null || device is { IsDisabled: false } || (device is { IsPresent: false } && !owed.Persistent))
            {
                // Taken over by a lock, on again, or away (a disable that isn't persistent is gone with it).
                _owed.Remove(owed);
                changed = true;
                continue;
            }

            if (device is { IsPresent: false })
            {
                owed.NextTry = _clock.UtcNow; // A persistent disable is undone as soon as the device is back.
                continue;
            }

            if (_journal is not null && device is not null && InScope(device, _journal.Request, _devices))
            {
                // The lock wants it off too, but couldn't record it yet (its record can't be saved): left off, and
                // still owed for after the unlock.
                continue;
            }

            if (_clock.UtcNow < owed.NextTry)
            {
                continue;
            }

            if (TurnOn(owed.InstanceId, owed.Persistent, out PnpChange change, out bool away))
            {
                _owed.Remove(owed);
                changed = true;
                if (!away)
                {
                    string noun = Noun(owed.Kind);
                    Alert(EngineAlertKind.DeferredRestoreDone, owed.Name, $"A {noun} BlindsDown couldn't turn back on at unlock is on again: {owed.Name}.", urgent: false);
                }
            }
            else if (change.Result == PnpChangeResult.KeptOff)
            {
                // Someone turned it off for good in the meantime: theirs now, left off, and never asked again.
                _owed.Remove(owed);
                changed = true;
            }
            else
            {
                owed.NextTry = _clock.UtcNow + RestoreRetryInterval;
            }
        }

        if (changed)
        {
            SaveOwed();
        }
    }

    /// <summary>
    /// An unlock while not locked: every owed restore is tried right away (the uninstaller and --restore ask for this,
    /// and must learn about a device that stays off). Null when nothing is owed.
    /// </summary>
    private RestoreReport? RestoreOwedNow()
    {
        bool listed = RefreshDevices();
        if (_owed.Count == 0)
        {
            return null;
        }

        var lines = new List<RestoreLine>();
        foreach (OwedRestore owed in _owed.ToList())
        {
            PnpDevice? device = listed ? Find(owed.InstanceId) : null;
            RestoreLine Line(RestoreOutcome outcome, string message) => new(outcome, owed.Name, message, owed.Kind);
            if (device is { IsDisabled: false } || (device is { IsPresent: false } && !owed.Persistent))
            {
                _owed.Remove(owed);
                lines.Add(Line(RestoreOutcome.Exact, device.IsPresent ? "On, as before the lock." : NotConnectedLine));
            }
            else if (device is { IsPresent: false })
            {
                lines.Add(Line(RestoreOutcome.Pending, PersistentAwayLine));
            }
            else if (TurnOn(owed.InstanceId, owed.Persistent, out PnpChange change, out bool away))
            {
                _owed.Remove(owed);
                lines.Add(Line(RestoreOutcome.Exact, away ? NotConnectedLine : "Turned back on, as before the lock."));
            }
            else if (change.Result == PnpChangeResult.KeptOff)
            {
                _owed.Remove(owed);
                lines.Add(Line(RestoreOutcome.ChangedByOthers, KeptOffLine));
            }
            else
            {
                owed.NextTry = _clock.UtcNow + RestoreRetryInterval;
                lines.Add(Line(RestoreOutcome.Failed, $"Could not be turned back on ({TurnOnProblem(change)}). {Advice(owed.Persistent)}"));
            }
        }

        SaveOwed();
        return new RestoreReport(lines, lines.Count, CoversCameras: true);
    }

    /// <summary>
    /// One urgent alert, never held back by the cooldown, for the devices a change of request took out of the lock:
    /// <paramref name="turnedOn"/> were off and are on again; <paramref name="stopped"/> were still being turned off
    /// (in use, or unplugged) and no longer are.
    /// </summary>
    private void AnnounceReleased(List<(string Name, DeviceKind Kind)> turnedOn, List<(string Name, DeviceKind Kind)> stopped)
    {
        int count = turnedOn.Count + stopped.Count;
        if (count == 0)
        {
            return;
        }

        static string Names(List<(string Name, DeviceKind Kind)> devices) => string.Join(", ", devices.Select(d => d.Name));
        string what = count == 1 ? $"a {Noun(turnedOn.Concat(stopped).First().Kind)}" : $"{count} devices";
        string them = count == 1 ? "it" : "them";
        string done = (turnedOn.Count, stopped.Count) switch
        {
            (_, 0) => $"turned {them} back on: {Names(turnedOn)}",
            (0, _) => $"no longer turns {them} off: {Names(stopped)}",
            _ => $"turned {Names(turnedOn)} back on and no longer turns {Names(stopped)} off",
        };
        Alert(
            EngineAlertKind.ReEnabledWhileLocked,
            string.Join(", ", turnedOn.Concat(stopped).Select(d => d.Name)),
            $"{NarrowedAlertPrefix} {what} out, so BlindsDown {done}. If you didn't change the lock, unlock and lock again, and check which programs are running.",
            urgent: true,
            always: true);
    }

    private void AnnounceReturn(LockAction action, PnpDevice device)
    {
        string noun = Noun(device.Kind);
        if (action.Status == ActionStatus.Applied)
        {
            Alert(EngineAlertKind.DeviceRelocked, device.Name, $"A {noun} was plugged back in while locked and was on for a moment. BlindsDown turned it off again: {device.Name}.", urgent: false);
        }
        else
        {
            Alert(EngineAlertKind.DeviceRelocked, device.Name, $"A {noun} was plugged back in while locked, and BlindsDown couldn't turn it off: {device.Name}. {action.Note}", urgent: true);
        }
    }

    private void AnnounceReEnabled(LockAction action, PnpDevice device)
    {
        // The count is saved with the alerts below (at most every AlertCooldown per device), not on every
        // re-disable: something that fights the lock must not cause a stream of flushed journal writes.
        action.GuardCount++;
        DateTimeOffset now = _clock.UtcNow;
        if (!_guardTimes.TryGetValue(action.TargetId, out List<DateTimeOffset>? times))
        {
            times = [];
            _guardTimes[action.TargetId] = times;
        }

        times.Add(now);
        times.RemoveAll(t => now - t > TamperWindow);
        string noun = Noun(device.Kind);
        bool alerted;
        if (times.Count >= TamperThreshold)
        {
            alerted = Alert(
                EngineAlertKind.Tampering,
                device.Name,
                $"Something keeps turning a {noun} back on ({times.Count} times in the last minute). BlindsDown keeps turning it off; check which app is doing this: {device.Name}.",
                urgent: true);
        }
        else if (action.Status == ActionStatus.Applied)
        {
            alerted = Alert(EngineAlertKind.ReEnabledWhileLocked, device.Name, $"Something turned a {noun} back on while locked. BlindsDown turned it off again: {device.Name}.", urgent: true);
        }
        else
        {
            alerted = Alert(EngineAlertKind.ReEnabledWhileLocked, device.Name, $"Something turned a {noun} back on while locked, and BlindsDown couldn't turn it off again: {device.Name}. {action.Note}", urgent: true);
        }

        if (alerted)
        {
            TrySave();
        }
    }

    private void AlertFailure(LockAction action, DeviceKind kind, PnpChangeResult result)
    {
        if (!_failureAlerts.Add(action.TargetId + "|" + result))
        {
            return; // Already told in this lock; the status keeps showing it.
        }

        string noun = Noun(kind);
        string name = action.DisplayName;
        if (result == PnpChangeResult.Vetoed)
        {
            Alert(EngineAlertKind.DeviceBusy, name, $"A {noun} is in use by an app, so Windows won't turn it off yet. BlindsDown keeps trying: {name}.", urgent: true);
        }
        else
        {
            Alert(EngineAlertKind.LockFailed, name, $"BlindsDown couldn't turn off a {noun}: {name}. {action.Note}", urgent: true);
        }
    }

    private bool RetryDue(string instanceId) =>
        !_failures.TryGetValue(instanceId, out Failure? failure)
        || failure.Result == PnpChangeResult.Vetoed
        || _clock.UtcNow - failure.At >= FailureRetryInterval;

    /// <summary>
    /// False only when BlindsDown certainly has not left this device off: not touched in this lock yet (Planned)
    /// or already turned back on (Reverted). A failed disable counts too: Windows can finish a refused change
    /// late, and the device was on before the lock, so turning it on at unlock is always right.
    /// </summary>
    private static bool MayBeOurs(ActionStatus status) =>
        status is not (ActionStatus.Planned or ActionStatus.Reverted);

    private PnpChange Change(string instanceId, bool disable, bool clearPersistent = false)
    {
        try
        {
            return disable ? _backend.Disable(instanceId) : _backend.Enable(instanceId, clearPersistent);
        }
        catch (Exception ex) when (IsBackendFailure(ex))
        {
            bool denied = ex is UnauthorizedAccessException or DeviceOperationException { ErrorKind: DeviceErrorKind.AccessDenied };
            return new PnpChange(denied ? PnpChangeResult.AccessDenied : PnpChangeResult.Failed, ex.Message);
        }
    }

    /// <summary>Reads the device list. False (and the last list is kept) when Windows could not list them.</summary>
    private bool RefreshDevices()
    {
        IReadOnlyList<PnpDevice> listed;
        try
        {
            listed = _backend.Enumerate();
        }
        catch (Exception ex) when (IsBackendFailure(ex))
        {
            _listFailed = true;
            return false;
        }

        // Only cameras and microphone hardware are this engine's business, each once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new List<PnpDevice>();
        foreach (PnpDevice? device in listed ?? [])
        {
            if (device is { Kind: DeviceKind.Camera or DeviceKind.MicrophoneHardware }
                && !string.IsNullOrEmpty(device.InstanceId)
                && seen.Add(device.InstanceId))
            {
                devices.Add(string.IsNullOrWhiteSpace(device.Name) ? device with { Name = device.InstanceId } : device);
            }
        }

        _devices = devices;
        _listFailed = false;
        return true;
    }

    /// <summary>What a backend may throw for a device it can't handle. Anything else is a bug and propagates.</summary>
    private static bool IsBackendFailure(Exception ex) =>
        ex is DeviceOperationException or ExternalException or InvalidOperationException or UnauthorizedAccessException
            or IOException or ArgumentException or TimeoutException or NotSupportedException;

    private PnpDevice? Find(string instanceId) => _devices.FirstOrDefault(d => SameId(d.InstanceId, instanceId));

    private LockAction? FindAction(string instanceId) => _journal?.Actions.Find(a => SameId(a.TargetId, instanceId));

    private bool IsOwed(string instanceId) => FindOwed(instanceId) is not null;

    private OwedRestore? FindOwed(string instanceId) => _owed.Find(o => SameId(o.InstanceId, instanceId));

    /// <summary>Remembers a device BlindsDown still has to turn back on. False when it already was owed.</summary>
    private bool Owe(string instanceId, string name, DeviceKind kind, bool persistent, DateTimeOffset nextTry)
    {
        if (IsOwed(instanceId))
        {
            return false;
        }

        _owed.Add(new OwedRestore(instanceId, name, kind, persistent, nextTry));
        return true;
    }

    /// <summary>What to do about a device that could not be turned back on.</summary>
    private static string Advice(bool persistent) => persistent ? DeviceManagerAdvice : RestartAdvice;

    /// <summary>Takes over the owed restores an earlier run of the helper saved, due right away.</summary>
    private void LoadOwed()
    {
        IReadOnlyList<OwedDevice> saved;
        try
        {
            saved = _journalStore.LoadOwedRestores();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JournalCorruptException)
        {
            return; // They only aren't retried; the status shows them as turned off by the user.
        }

        foreach (OwedDevice device in saved)
        {
            if (!IsOwed(device.InstanceId))
            {
                _owed.Add(new OwedRestore(device.InstanceId, device.Name, device.Kind, device.Persistent, _clock.UtcNow));
            }
        }
    }

    /// <summary>Saves the owed restores. A failure is retried on every pass: until it works, only a restart of the
    /// helper would forget them.</summary>
    private void SaveOwed()
    {
        try
        {
            _journalStore.SaveOwedRestores([.. _owed.Select(o => new OwedDevice { InstanceId = o.InstanceId, Name = o.Name, Kind = o.Kind, Persistent = o.Persistent })]);
            _owedNotSaved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owedNotSaved = true;
        }
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Noun(DeviceKind kind) => kind == DeviceKind.MicrophoneHardware ? "microphone" : "camera";

    private static string TurnOffProblem(PnpChange change) => change.Result switch
    {
        PnpChangeResult.Vetoed => InUseProblem,
        PnpChangeResult.NeedsRestart => "Windows can only turn it off after a restart. Restart the PC to lock it.",
        PnpChangeResult.AccessDenied => "Windows didn't allow BlindsDown to turn it off.",
        PnpChangeResult.Done => "Windows said it was turned off, but it is still on.",
        _ => Detail(change) is { } detail ? $"Windows couldn't turn it off ({detail})." : "Windows couldn't turn it off.",
    };

    /// <summary>Why a device could not be turned back on, for "Could not be turned back on (...)".</summary>
    private static string TurnOnProblem(PnpChange change) => change.Result switch
    {
        PnpChangeResult.Done => "Windows said it was turned on, but it is still off",
        PnpChangeResult.Vetoed => "Windows refused",
        PnpChangeResult.NeedsRestart => "Windows needs a restart for it",
        PnpChangeResult.AccessDenied => "Windows didn't allow it",
        _ => Detail(change) ?? "Windows couldn't do it",
    };

    /// <summary>The backend's detail, short enough for a status line and without a trailing period.</summary>
    private static string? Detail(PnpChange change)
    {
        string? detail = change.Detail?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        return detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength].TrimEnd() + "...";
    }

    private static ActionState Snapshot(LockAction action) => new(action.Status, action.Note, action.WasAlreadyLocked);

    /// <summary>Saved only on a change: the guard and the safety poll get here often, and a device that stays away
    /// or keeps failing must not cause a stream of disk writes.</summary>
    private void SaveIfChanged(LockAction action, ActionState before)
    {
        if (Snapshot(action) != before)
        {
            TrySave();
        }
    }

    private void SetPhase(LockPhase phase)
    {
        if (_journal is not null && _journal.Phase != phase)
        {
            _journal.Phase = phase;
            TrySave();
        }
    }

    /// <summary>
    /// Saves the journal; on failure (disk full, folder blocked) raises an urgent alert instead of throwing, and
    /// returns false so callers never change a device the journal doesn't know about.
    /// </summary>
    private bool TrySave()
    {
        if (_journal is null)
        {
            return true;
        }

        try
        {
            _journalStore.Save(_journal);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Alert(EngineAlertKind.LockFailed, LockRecord, $"BlindsDown can't save its camera lock record ({ex.Message}). Until it can, it won't turn off any more devices.", urgent: true);
            return false;
        }
    }

    private void ForgetLockMemory()
    {
        _guardTimes.Clear();
        _failures.Clear();
        _failureAlerts.Clear();
        _away.Clear();
        _releaseWhenListed.Clear();
        _quietPassPending = false;
        _releasePending = false;
        _releaseNews = ReleaseNews.Everything;
    }

    /// <summary>Deletes the lock record of a finished unlock. Returns null when it is gone, or why it isn't.</summary>
    private string? TryDeleteRecord()
    {
        try
        {
            _journalStore.Delete();
            _recordLeftOver = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _recordLeftOver = true;
            return ex.Message;
        }
    }

    /// <summary>Raises an alert unless the same kind of alert for the same device is cooling down (never for
    /// <paramref name="always"/>). Returns false when it was throttled.</summary>
    private bool Alert(EngineAlertKind kind, string deviceName, string message, bool urgent, bool always = false)
    {
        string key = kind + "|" + deviceName;
        DateTimeOffset now = _clock.UtcNow;
        if (!always
            && kind is EngineAlertKind.ReEnabledWhileLocked or EngineAlertKind.Tampering or EngineAlertKind.LockFailed
            && _lastAlert.TryGetValue(key, out DateTimeOffset last)
            && now - last < AlertCooldown)
        {
            return false;
        }

        _lastAlert[key] = now;
        AlertRaised?.Invoke(new EngineAlert(kind, deviceName, message, urgent));
        return true;
    }

    private void PublishStatus()
    {
        DeviceLockStatus status = BuildStatus();
        string signature = Signature(status);
        Status = status;
        if (signature != _lastStatusSignature)
        {
            _lastStatusSignature = signature;
            StatusChanged?.Invoke(status);
        }
    }

    private DeviceLockStatus BuildStatus()
    {
        DeviceLockJournal? journal = _journal;
        var devices = new List<DeviceLockInfo>();
        var problems = new List<string>();
        bool partial = false;
        if (_listFailed)
        {
            // Nothing can be checked, so the lock must not look complete. The safety poll keeps trying.
            problems.Add(ListFailedProblem);
            partial = true;
        }

        foreach (PnpDevice device in _devices)
        {
            (DeviceProtection protection, string? problem) = Protection(device, journal);
            if (problem is not null)
            {
                problems.Add($"{device.Name}: {problem}");

                // A device BlindsDown couldn't turn back on is off: worth saying, but it leaves nothing unprotected.
                partial |= !(device.IsDisabled && IsOwed(device.InstanceId));
            }

            devices.Add(new DeviceLockInfo(
                device.InstanceId,
                device.Name,
                device.StableKey,
                device.Kind,
                device.IsVirtual,
                HasHardwareMute: false,
                protection,
                problem,
                device.Kind == DeviceKind.Camera ? CameraTypeOf(device) : null));
        }

        if (JournalWasCorrupt)
        {
            problems.Add(CorruptJournalProblem);
        }

        if (journal is null && _startProblem is not null)
        {
            problems.Add(_startProblem);
        }

        LockState state = journal?.Phase switch
        {
            null => LockState.Unlocked,
            LockPhase.Locking => LockState.Locking,
            LockPhase.Unlocking => LockState.Unlocking,
            _ => partial ? LockState.PartiallyLocked : LockState.Locked,
        };

        return new DeviceLockStatus(state, devices, problems);
    }

    private (DeviceProtection Protection, string? Problem) Protection(PnpDevice device, DeviceLockJournal? journal)
    {
        if (!device.IsPresent)
        {
            return (DeviceProtection.NotPresent, null); // Locked the moment it appears (it comes back on).
        }

        if (device.IsDisabled && FindOwed(device.InstanceId) is { } owed && FindAction(device.InstanceId) is null)
        {
            // Off because of BlindsDown, not the user, locked or not.
            return (DeviceProtection.None, owed.Persistent ? OwedPersistentProblem : OwedRestoreProblem);
        }

        if (journal is null)
        {
            return (device.IsDisabled ? DeviceProtection.DisabledByUser : DeviceProtection.None, null);
        }

        LockAction? action = FindAction(device.InstanceId);
        if (action is not null)
        {
            if (device.IsDisabled)
            {
                return (action.WasAlreadyLocked ? DeviceProtection.DisabledByUser : DeviceProtection.DeviceDisabled, null);
            }

            return journal.Phase == LockPhase.Locked ? (DeviceProtection.None, action.Note ?? NotYetProblem) : (DeviceProtection.None, null);
        }

        if (device.IsDisabled)
        {
            return (DeviceProtection.DisabledByUser, null);
        }

        if (IsExcluded(device, journal.Request, _devices))
        {
            return (DeviceProtection.Excluded, null);
        }

        if (journal.Phase != LockPhase.Locked || !IsRequested(device, journal.Request))
        {
            return (DeviceProtection.None, null); // An infrared or virtual camera left out is not a problem.
        }

        // Microphone hardware the app keeps on is not a problem either. A camera it keeps on is: its video is on.
        if (IsKeptOn(device, journal.Request))
        {
            return (DeviceProtection.None, device.Kind == DeviceKind.Camera ? KeptForMicrophoneProblem : null);
        }

        return (DeviceProtection.None, NotYetProblem); // Requested and still on.
    }

    private static string Signature(DeviceLockStatus status) =>
        status.State + "|"
        + string.Join(";", status.Devices.Select(d => $"{d.Id}={d.Name},{d.Kind},{d.Camera},{d.IsVirtual},{d.Protection}:{d.Problem}"))
        + "|" + string.Join(";", status.Problems);

    /// <summary>Which devices taken out of the lock by a change of request are announced, from least to most.</summary>
    private enum ReleaseNews
    {
        /// <summary>None: an unlock that was interrupted was turning them back on anyway.</summary>
        Nothing,

        /// <summary>Only devices turned back on: the helper started, and only re-checks the same request.</summary>
        TurnedOnOnly,

        /// <summary>Also devices the lock was still turning off (in use, or unplugged): the request changed.</summary>
        Everything,
    }

    /// <summary>What taking a device out of the lock changed for it.</summary>
    private enum Unguarded
    {
        /// <summary>Nothing: it stays off (the user's, or turned off for good by someone else).</summary>
        Nothing,

        /// <summary>It was off because of the lock and is on again.</summary>
        TurnedOn,

        /// <summary>It is on (in use) or unplugged (on when it is back), and the lock no longer turns it off.</summary>
        NoLongerTurnedOff,
    }

    /// <summary>What a journal entry says about its device; compared before and after a step to save only on a change.</summary>
    private readonly record struct ActionState(ActionStatus Status, string? Note, bool WasAlreadyLocked);

    private sealed record Failure(PnpChangeResult Result, DateTimeOffset At);

    private sealed class OwedRestore(string instanceId, string name, DeviceKind kind, bool persistent, DateTimeOffset nextTry)
    {
        public string InstanceId { get; } = instanceId;

        public string Name { get; } = name;

        public DeviceKind Kind { get; } = kind;

        /// <summary>Windows made BlindsDown's disable persistent: only an enable told so turns it back on.</summary>
        public bool Persistent { get; } = persistent;

        public DateTimeOffset NextTry { get; set; } = nextTry;
    }
}
