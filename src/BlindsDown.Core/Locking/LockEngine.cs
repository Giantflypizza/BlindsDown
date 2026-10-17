using BlindsDown.Core.Devices;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;

namespace BlindsDown.Core.Locking;

/// <summary>
/// The heart of BlindsDown: locks and unlocks microphones, guards them while locked, and restores
/// exactly what it changed. Every change is written to the journal before it is made, so a crash
/// at any point can be recovered.
///
/// Rules that keep the user's devices safe:
///  * Only devices that are working right now (active or unplugged) and not excluded are touched.
///    Disabled or missing devices are left alone, so devices the user disabled stay disabled.
///  * Unlock only undoes what BlindsDown did, and only if the device is still in the state BlindsDown
///    left it in.
///  * Volume levels, default devices and other settings are never written.
///
/// Not thread-safe: the app calls it from one dedicated thread.
/// </summary>
public sealed class LockEngine
{
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TamperWindow = TimeSpan.FromSeconds(60);
    private const int TamperThreshold = 5;
    private const string NotConnected = "Not connected";

    private readonly IMicBackend _mics;
    private readonly IJournalStore _journalStore;
    private readonly IPendingRestoreStore _pendingStore;
    private readonly IClock _clock;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _guardTimes = new(StringComparer.OrdinalIgnoreCase);

    private LockJournal? _journal;
    private string _lastStatusSignature = string.Empty;

    public LockEngine(IMicBackend mics, IJournalStore journalStore, IPendingRestoreStore pendingStore, IClock clock, Func<AppSettings> settings)
    {
        _mics = mics;
        _journalStore = journalStore;
        _pendingStore = pendingStore;
        _clock = clock;
        _settings = settings;
    }

    public event Action<LockStatusInfo>? StatusChanged;

    public event Action<EngineAlert>? AlertRaised;

    public LockStatusInfo Status { get; private set; } = LockStatusInfo.Unlocked;

    /// <summary>True while a lock exists (including half-finished ones after a crash).</summary>
    public bool HasLock => _journal is not null;

    /// <summary>Set when the journal file exists but could not be read.</summary>
    public bool JournalWasCorrupt { get; private set; }

    /// <summary>
    /// Call once at startup. Returns null when there was nothing to recover. Returns a recovery
    /// description when a previous lock is still on disk: either the app exited normally while
    /// locked (the lock simply continues) or it crashed (the UI must ask the user what to do;
    /// meanwhile the devices stay protected).
    /// </summary>
    public StartupRecovery? Initialize()
    {
        LockJournal? journal;
        try
        {
            journal = _journalStore.Load();
        }
        catch (JournalCorruptException)
        {
            JournalWasCorrupt = true;
            journal = null;
        }

        if (journal is null)
        {
            ProcessPendingRestores();
            PublishStatus(force: true);
            return null;
        }

        _journal = journal;
        bool clean = journal.Phase == LockPhase.Locked && journal.ExitedCleanlyWhileLocked;
        if (journal.ExitedCleanlyWhileLocked)
        {
            journal.ExitedCleanlyWhileLocked = false;
            TrySave();
        }

        if (journal.Phase == LockPhase.Locked)
        {
            Reconcile();
        }

        PublishStatus(force: true);
        return new StartupRecovery(
            journal.Phase,
            clean,
            journal.StartedAt,
            journal.Actions.Count(a => !a.WasAlreadyLocked && MayBeMutedByUs(a.Status)));
    }

    /// <summary>Locks every microphone that is in scope. Does nothing if already locked.</summary>
    public LockStatusInfo Lock()
    {
        if (_journal is not null)
        {
            if (_journal.Phase != LockPhase.Locked)
            {
                ResumeLock();
            }

            return Status;
        }

        AppSettings settings = _settings();
        AudioSnapshot snapshot = TakeSnapshot();
        List<LockAction> pending = _pendingStore.Load();
        bool tookPending = false;

        var journal = new LockJournal
        {
            LockId = Guid.NewGuid(),
            Phase = LockPhase.Locking,
            StartedAt = _clock.UtcNow,
            Before = snapshot,
        };

        // Non-default microphones first, defaults last, so apps that follow the default don't hop around.
        foreach (MicEndpoint mic in snapshot.Mics.OrderBy(m => snapshot.Defaults.Contains(m.Id) ? 1 : 0))
        {
            if (!mic.IsUsable || settings.IsExcluded(mic, snapshot.Mics))
            {
                continue;
            }

            LockAction? owed = TakePendingEntry(pending, mic.Id);
            tookPending |= owed is not null;
            journal.Actions.Add(NewAction(mic, owed, addedWhileLocked: false));
        }

        // Journal first: if it can't be written, nothing has been changed and we stay unlocked. Then the
        // pending list it took entries from (only if it did: a pending file that failed to load must not
        // be overwritten). A crash in between leaves an entry in both; unlock clears such leftovers.
        _journalStore.Save(journal);
        _journal = journal;
        if (tookPending)
        {
            _pendingStore.Save(pending);
        }

        PublishStatus();

        foreach (LockAction action in journal.Actions)
        {
            Apply(action);
        }

        journal.Phase = LockPhase.Locked;
        TrySave();
        PublishStatus();
        return Status;
    }

    /// <summary>Finishes an interrupted lock, or re-locks after an interrupted unlock.</summary>
    public void ResumeLock()
    {
        if (_journal is null)
        {
            return;
        }

        _journal.Phase = LockPhase.Locking;
        TrySave();
        foreach (LockAction action in _journal.Actions)
        {
            if (action.Status != ActionStatus.Applied)
            {
                Apply(action);
            }
        }

        _journal.Phase = LockPhase.Locked;
        TrySave();
        Reconcile();
        PublishStatus(force: true);
    }

    /// <summary>
    /// Undoes everything the lock did, in reverse order, and reports how close to the original state
    /// the devices are. Works from any phase, including after a crash. Returns null if not locked.
    /// </summary>
    public RestoreReport? Unlock()
    {
        LockJournal? journal = _journal;
        if (journal is null)
        {
            return null;
        }

        journal.Phase = LockPhase.Unlocking;
        TrySave();
        PublishStatus();

        var stillPending = new List<LockAction>();
        for (int i = journal.Actions.Count - 1; i >= 0; i--)
        {
            LockAction action = journal.Actions[i];
            Revert(action);
            TrySave();
            if (action.Status == ActionStatus.RevertPending)
            {
                stillPending.Add(action);
            }
        }

        AudioSnapshot after = TakeSnapshot();
        RestoreReport report = RestoreReport.Build(journal, after);

        // This unlock decides the outcome for every device whose journal entry agrees that it was unmuted
        // before: older pending entries for them are leftovers of an interrupted lock or unlock and, kept,
        // would later unmute a device the user has muted since. An entry the journal disagrees with (this
        // lock first saw the device muted) predates this lock and was not taken over because its file
        // could not be read then: the mute this lock saw was BlindsDown's own, so the entry stays.
        List<LockAction> pending = _pendingStore.Load();
        int superseded = pending.RemoveAll(p => journal.Actions.Exists(a => !a.WasAlreadyLocked && SameId(a.TargetId, p.TargetId)));
        pending.AddRange(stillPending);
        if (superseded > 0 || stillPending.Count > 0)
        {
            _pendingStore.Save(pending);
        }

        try
        {
            _journalStore.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The devices are restored; only the file is left. The next start sees an "interrupted
            // unlock", finds everything already reverted and just tidies up.
            Alert(EngineAlertKind.LockFailed, "lock record", $"Your microphones are restored, but BlindsDown couldn't delete its lock record ({ex.Message}).", urgent: false);
        }

        _journal = null;
        _guardTimes.Clear();
        PublishStatus(force: true);
        return report;
    }

    /// <summary>Records that the app is exiting normally while locked, so the next start resumes quietly.</summary>
    public void MarkCleanExit()
    {
        if (_journal is { Phase: LockPhase.Locked })
        {
            _journal.ExitedCleanlyWhileLocked = true;
            TrySave();
        }
    }

    /// <summary>A microphone's mute state changed. <paramref name="causedByUs"/> is true for our own changes.</summary>
    public void OnMuteChanged(string endpointId, bool muted, bool causedByUs)
    {
        if (causedByUs || muted || _journal is not { Phase: LockPhase.Locked })
        {
            return;
        }

        IReadOnlyList<MicEndpoint> all = SafeEnumerate();
        MicEndpoint? mic = all.FirstOrDefault(m => SameId(m.Id, endpointId));
        if (mic is not null && _settings().IsExcluded(mic, all))
        {
            return; // Excluded (possibly after the lock started): the user wants to use it. Unlock still restores it.
        }

        LockAction? action = FindAction(endpointId);
        if (action is null)
        {
            if (mic is not null && mic.IsUsable)
            {
                AddWhileLocked(mic);
            }

            return;
        }

        Reapply(action, EngineAlertKind.UnmutedWhileLocked, $"Something unmuted \"{action.DisplayName}\" while locked. BlindsDown muted it again.");
    }

    /// <summary>A microphone was added, removed, plugged in, unplugged, enabled or disabled.</summary>
    public void OnEndpointChanged(string endpointId)
    {
        if (_journal is null)
        {
            ProcessPendingRestores();
            PublishStatus();
            return;
        }

        if (_journal.Phase != LockPhase.Locked)
        {
            return; // Lock/unlock in progress; it verifies at the end.
        }

        IReadOnlyList<MicEndpoint> all = SafeEnumerate();
        MicEndpoint? mic = all.FirstOrDefault(m => SameId(m.Id, endpointId));
        if (mic is not null && mic.IsUsable && !_settings().IsExcluded(mic, all))
        {
            LockAction? action = FindAction(mic.Id);
            if (action is null)
            {
                AddWhileLocked(mic);
            }
            else if (action.Status != ActionStatus.Applied || mic.Muted == false)
            {
                // Not locked yet, or it came back unmuted. (A locked device that merely can't be read right
                // now, e.g. a Bluetooth hands-free mic that disconnected, keeps its lock until it returns.)
                bool cameBackUnmuted = action.Status == ActionStatus.Applied;
                Apply(action);
                if (cameBackUnmuted && action.Status == ActionStatus.Applied)
                {
                    Alert(EngineAlertKind.DeviceRelocked, action.DisplayName, $"\"{action.DisplayName}\" came back unmuted. BlindsDown muted it again.", urgent: false);
                }
            }
        }

        PublishStatus();
    }

    /// <summary>
    /// Safety net, called every few seconds: makes sure every in-scope microphone is still muted
    /// (in case a notification was missed) and finishes deferred restores.
    /// </summary>
    public void Reconcile()
    {
        if (_journal is null)
        {
            ProcessPendingRestores();
            PublishStatus();
            return;
        }

        if (_journal.Phase != LockPhase.Locked)
        {
            PublishStatus();
            return;
        }

        AppSettings settings = _settings();
        IReadOnlyList<MicEndpoint> mics = SafeEnumerate();
        foreach (MicEndpoint mic in mics)
        {
            if (!mic.IsUsable || settings.IsExcluded(mic, mics))
            {
                continue;
            }

            LockAction? action = FindAction(mic.Id);
            if (action is null)
            {
                AddWhileLocked(mic);
            }
            else if (action.Status != ActionStatus.Applied)
            {
                // Not locked yet (just became readable) or the mute failed: try again. A mute that
                // doesn't stick is a device problem, already reported, not something fighting us.
                Apply(action);
            }
            else if (mic.Muted == false)
            {
                Reapply(action, EngineAlertKind.UnmutedWhileLocked, $"\"{action.DisplayName}\" was unmuted while locked. BlindsDown muted it again.");
            }
        }

        PublishStatus();
    }

    /// <summary>Forces a fresh status event (e.g. after settings such as exclusions changed).</summary>
    public void RefreshStatus() => PublishStatus(force: true);

    private void AddWhileLocked(MicEndpoint mic)
    {
        List<LockAction> pending = _pendingStore.Load();
        LockAction? owed = TakePendingEntry(pending, mic.Id);
        LockAction action = NewAction(mic, owed, addedWhileLocked: true);

        _journal!.Actions.Add(action);
        if (!TrySave())
        {
            // Not recorded, so it must not be touched: muting it now could never be undone after a crash.
            _journal.Actions.Remove(action);
            PublishStatus();
            return;
        }

        if (owed is not null)
        {
            _pendingStore.Save(pending);
        }

        Apply(action);
        if (action.Status == ActionStatus.Applied)
        {
            Alert(EngineAlertKind.NewDeviceLocked, mic.Name, $"\"{mic.Name}\" was connected while locked. BlindsDown muted it.", urgent: false);
        }

        PublishStatus();
    }

    private void Apply(LockAction action)
    {
        (ActionStatus Status, string? Note, bool WasAlreadyLocked) before = (action.Status, action.Note, action.WasAlreadyLocked);
        bool touched = MayBeMutedByUs(action.Status);
        string? failure = null;
        try
        {
            bool? current = _mics.GetMute(action.TargetId);
            if (current is null)
            {
                // Can't be opened right now (e.g. unplugged). It gets locked when it appears. A device
                // BlindsDown may already have muted keeps its status, so unlock still undoes that mute.
                if (!touched)
                {
                    action.Status = ActionStatus.Planned;
                    action.Note = NotConnected;
                }
            }
            else
            {
                if (!touched)
                {
                    // Untouched by this lock so far, so its state right now is its state right before our
                    // change, and that is what unlock must restore. It can differ from the snapshot: something
                    // changed it in between, or it could not be read then (Bluetooth hands-free, empty jacks).
                    action.WasAlreadyLocked = current == true;
                }

                if (current == true)
                {
                    action.Status = ActionStatus.Applied;
                    action.Note = null;
                }
                else
                {
                    bool recorded = true;
                    if (!touched)
                    {
                        // Write-ahead: once the journal says "Applying", a crash right after the mute below
                        // still gets it undone. If that record can't be written, the mic is not touched.
                        action.Status = ActionStatus.Applying;
                        recorded = TrySave();
                        if (recorded)
                        {
                            touched = true;
                        }
                        else
                        {
                            action.Status = before.Status;
                            action.Note = failure = "BlindsDown can't save its lock record, so it did not mute this microphone";
                        }
                    }

                    if (recorded)
                    {
                        _mics.SetMute(action.TargetId, true);
                        bool verified = _mics.GetMute(action.TargetId) == true;
                        action.Status = verified ? ActionStatus.Applied : ActionStatus.ApplyFailed;
                        action.Note = failure = verified ? null : "Mute did not take effect";
                    }
                }
            }
        }
        catch (DeviceOperationException ex)
        {
            // Failing before the write-ahead means the first read failed and nothing was changed: the
            // device stays "not touched yet", so its original state still comes from its first good
            // reading. (As ApplyFailed it would keep the snapshot's guess, and unlock could unmute a
            // Bluetooth mic the user had muted.)
            action.Status = touched ? ActionStatus.ApplyFailed : ActionStatus.Planned;
            action.Note = failure = ex.Message;
        }

        // Saved only on a change: the guard and the safety poll get here often, and a device that stays
        // away, keeps failing or is fought over by another app must not cause a stream of disk writes.
        if ((action.Status, action.Note, action.WasAlreadyLocked) != before)
        {
            TrySave();
        }

        if (failure is not null)
        {
            Alert(EngineAlertKind.LockFailed, action.DisplayName, $"Could not mute \"{action.DisplayName}\": {failure}", urgent: true);
        }
    }

    private void Reapply(LockAction action, EngineAlertKind kind, string message)
    {
        Apply(action);

        // The count is saved with the alerts below (at most every AlertCooldown per device), not on every
        // re-mute: an app that unmutes the mic whenever BlindsDown mutes it must not cause a stream of
        // flushed journal writes. Losing a few counts in a crash is harmless.
        action.GuardCount++;

        DateTimeOffset now = _clock.UtcNow;
        if (!_guardTimes.TryGetValue(action.TargetId, out List<DateTimeOffset>? times))
        {
            times = [];
            _guardTimes[action.TargetId] = times;
        }

        times.Add(now);
        times.RemoveAll(t => now - t > TamperWindow);
        bool alerted = false;
        if (times.Count >= TamperThreshold)
        {
            alerted = Alert(
                EngineAlertKind.Tampering,
                action.DisplayName,
                $"Something keeps unmuting \"{action.DisplayName}\" ({times.Count} times in the last minute). BlindsDown keeps muting it, but check which app is doing this.",
                urgent: true);
        }
        else if (action.Status == ActionStatus.Applied)
        {
            alerted = Alert(kind, action.DisplayName, message, urgent: true);
        }

        if (alerted)
        {
            TrySave();
        }

        PublishStatus();
    }

    private void Revert(LockAction action)
    {
        if (action.Status == ActionStatus.Reverted)
        {
            return; // Done in an earlier, interrupted unlock. (A failed unmute is simply tried again.)
        }

        if (action.WasAlreadyLocked)
        {
            action.Status = ActionStatus.Reverted;
            action.Note = "Was already muted before the lock; left muted.";
            return;
        }

        bool mayBeMutedByUs = MayBeMutedByUs(action.Status);
        try
        {
            bool? current = _mics.GetMute(action.TargetId);
            if (current is null)
            {
                // Can't reach it now. If we may have muted it, finish the job when it comes back.
                action.Status = mayBeMutedByUs ? ActionStatus.RevertPending : ActionStatus.Reverted;
                return;
            }

            if (current == false || !mayBeMutedByUs)
            {
                // Already unmuted, or we never changed it: nothing to undo.
                action.Status = ActionStatus.Reverted;
                return;
            }

            _mics.SetMute(action.TargetId, false);
            bool? verify = _mics.GetMute(action.TargetId);
            action.Status = verify == false ? ActionStatus.Reverted : ActionStatus.RevertFailed;
            action.Note = verify == false ? null : "Unmute did not take effect";
        }
        catch (DeviceOperationException ex)
        {
            action.Status = ActionStatus.RevertFailed;
            action.Note = ex.Message;
        }
    }

    /// <summary>Unmutes devices that were unplugged when the last lock ended, once they are back.</summary>
    private void ProcessPendingRestores()
    {
        List<LockAction> pending = _pendingStore.Load();
        if (pending.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (LockAction action in pending.ToList())
        {
            MicEndpoint? mic = FindMic(action.TargetId);
            if (mic is null || !mic.IsUsable)
            {
                continue;
            }

            Revert(action);
            if (action.Status == ActionStatus.RevertPending)
            {
                continue;
            }

            pending.Remove(action);
            changed = true;
            if (action.Status == ActionStatus.Reverted)
            {
                Alert(EngineAlertKind.DeferredRestoreDone, action.DisplayName, $"\"{action.DisplayName}\" is back and was restored (unmuted, as before the lock).", urgent: false);
            }
            else
            {
                Alert(EngineAlertKind.LockFailed, action.DisplayName, $"Could not restore \"{action.DisplayName}\": {action.Note?.TrimEnd('.')}. Unmute it in Windows Sound settings.", urgent: true);
            }
        }

        if (changed)
        {
            _pendingStore.Save(pending);
        }
    }

    /// <summary>Removes and returns the pending restore an earlier lock still owes this device, if any.</summary>
    private static LockAction? TakePendingEntry(List<LockAction> pending, string endpointId)
    {
        LockAction? entry = pending.Find(p => p.Kind == ActionKind.MuteMic && SameId(p.TargetId, endpointId));
        if (entry is not null)
        {
            pending.Remove(entry);
        }

        return entry;
    }

    /// <summary>
    /// The journal entry for a device about to be locked. A device an earlier lock still owes a restore
    /// is still muted by BlindsDown: its true original state comes from that entry, and it starts as
    /// "may already be muted by us" so that unlock (or a crash) can never leave it muted.
    /// </summary>
    private static LockAction NewAction(MicEndpoint mic, LockAction? owedRestore, bool addedWhileLocked) => new()
    {
        Kind = ActionKind.MuteMic,
        TargetId = mic.Id,
        DisplayName = mic.Name,
        WasAlreadyLocked = owedRestore?.WasAlreadyLocked ?? mic.Muted == true,
        Status = owedRestore is null ? ActionStatus.Planned : ActionStatus.Applying,
        AddedWhileLocked = addedWhileLocked,
    };

    /// <summary>
    /// False only when BlindsDown certainly has not left its own mute on the device: not touched in this
    /// lock yet (Planned) or already undone (Reverted). Anything else must be undone at unlock.
    /// </summary>
    private static bool MayBeMutedByUs(ActionStatus status) =>
        status is not (ActionStatus.Planned or ActionStatus.Reverted);

    private AudioSnapshot TakeSnapshot()
    {
        IReadOnlyList<MicEndpoint> mics = SafeEnumerate();
        DefaultMics defaults;
        try
        {
            defaults = _mics.GetDefaultMics();
        }
        catch (DeviceOperationException)
        {
            defaults = DefaultMics.None;
        }

        return new AudioSnapshot(mics, defaults, _clock.UtcNow);
    }

    /// <summary>Every microphone, or null when Windows could not list them.</summary>
    private IReadOnlyList<MicEndpoint>? TryEnumerate()
    {
        try
        {
            return _mics.EnumerateMics();
        }
        catch (DeviceOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<MicEndpoint> SafeEnumerate() => TryEnumerate() ?? [];

    private MicEndpoint? FindMic(string endpointId) =>
        SafeEnumerate().FirstOrDefault(m => SameId(m.Id, endpointId));

    private LockAction? FindAction(string endpointId) =>
        _journal?.Actions.Find(a => a.Kind == ActionKind.MuteMic && SameId(a.TargetId, endpointId));

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        if (_journal is not null)
        {
            _journalStore.Save(_journal);
        }
    }

    /// <summary>
    /// Saves the journal; on failure (disk full, folder blocked) raises an urgent alert instead of
    /// throwing, and returns false so callers never change a device the journal doesn't know about.
    /// </summary>
    private bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Alert(EngineAlertKind.LockFailed, "lock record", $"BlindsDown can't save its lock record ({ex.Message}). Free some disk space; until then, new microphones can't be locked.", urgent: true);
            return false;
        }
    }

    /// <summary>Raises an alert unless the same kind of alert for the same device is cooling down.
    /// Returns false when it was throttled.</summary>
    private bool Alert(EngineAlertKind kind, string deviceName, string message, bool urgent)
    {
        string key = kind + "|" + deviceName;
        DateTimeOffset now = _clock.UtcNow;
        if (kind is EngineAlertKind.UnmutedWhileLocked or EngineAlertKind.Tampering or EngineAlertKind.LockFailed
            && _lastAlert.TryGetValue(key, out DateTimeOffset last)
            && now - last < AlertCooldown)
        {
            return false;
        }

        _lastAlert[key] = now;
        AlertRaised?.Invoke(new EngineAlert(kind, deviceName, message, urgent));
        return true;
    }

    private void PublishStatus(bool force = false)
    {
        LockStatusInfo status = BuildStatus();
        string signature = Signature(status);
        Status = status;
        if (force || signature != _lastStatusSignature)
        {
            _lastStatusSignature = signature;
            StatusChanged?.Invoke(status);
        }
    }

    private LockStatusInfo BuildStatus()
    {
        AppSettings settings = _settings();
        IReadOnlyList<MicEndpoint>? mics = TryEnumerate();
        LockJournal? journal = _journal;
        var devices = new List<DeviceLockInfo>();
        var problems = new List<string>();
        if (mics is null && journal is { Phase: LockPhase.Locked })
        {
            // Nothing can be checked, so the lock must not look complete. The safety poll keeps trying.
            problems.Add("Windows could not list the microphones; BlindsDown keeps trying.");
        }

        foreach (MicEndpoint mic in mics ?? [])
        {
            LockAction? action = FindAction(mic.Id);
            bool relevant = mic.IsUsable || mic.State == EndpointState.Disabled || action is not null;
            if (!relevant)
            {
                continue;
            }

            DeviceProtection protection;
            string? problem = null;
            if (mic.State == EndpointState.Disabled)
            {
                protection = DeviceProtection.DisabledByUser;
            }
            else if (settings.IsExcluded(mic, mics!))
            {
                protection = DeviceProtection.Excluded;
            }
            else if (journal is null)
            {
                protection = DeviceProtection.None;
            }
            else if (!mic.IsUsable || (mic.State == EndpointState.Unplugged && mic.Muted is null))
            {
                // Not connected (or a Bluetooth hands-free mic or jack that can't be opened right now).
                // It is locked the moment it appears.
                protection = DeviceProtection.NotPresent;
            }
            else if (action is { Status: ActionStatus.Applied } && mic.Muted == true)
            {
                protection = mic.HasHardwareMute ? DeviceProtection.HardwareMute : DeviceProtection.SoftwareMute;
            }
            else if (journal.Phase != LockPhase.Locked)
            {
                protection = DeviceProtection.None; // Still being locked or unlocked.
            }
            else
            {
                protection = DeviceProtection.None;
                problem = action?.Note ?? "Not muted";
                problems.Add($"{mic.Name}: {problem}");
            }

            devices.Add(new DeviceLockInfo(mic.Id, mic.Name, mic.StableKey, DeviceKind.Microphone, mic.IsVirtual, mic.HasHardwareMute, protection, problem));
        }

        LockState state = journal?.Phase switch
        {
            null => LockState.Unlocked,
            LockPhase.Locking => LockState.Locking,
            LockPhase.Unlocking => LockState.Unlocking,
            _ => problems.Count == 0 ? LockState.Locked : LockState.PartiallyLocked,
        };

        return new LockStatusInfo(state, devices, problems);
    }

    private static string Signature(LockStatusInfo status) =>
        status.State + ";" + string.Join(";", status.Devices.Select(d => $"{d.Id}={d.Protection}:{d.Problem}"));
}
