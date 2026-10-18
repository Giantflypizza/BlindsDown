using BlindsDown.Core.Devices;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;

namespace BlindsDown.Core.Locking;

/// <summary>
/// The heart of BlindsDown: locks and unlocks microphones, guards them while locked, and restores
/// exactly what it changed. Every change is written to the journal before it is made, so a crash
/// at any point can be recovered.
///
/// A lock mutes every microphone in scope. The stronger lock (<see cref="AppSettings.DisableMicsInWindows"/>,
/// read when a lock starts) then also turns physical microphones off in Windows, the Sound settings
/// "Don't allow" switch; virtual ones are only muted (they go silent when the physical ones are off).
/// A microphone is muted first and turned off second, because turning it off doesn't cut the streams
/// apps already have open: those only ever hear silence. So its mute is checked again right before it is
/// turned off. Unlock turns it back on first, then unmutes it.
///
/// Rules that keep the user's devices safe:
///  * Only devices that are working right now (active or unplugged) and not excluded are touched.
///    Disabled or missing devices are left alone, so devices the user disabled stay disabled. Once a
///    microphone is in the lock it stays in, unless an exclusion made during the lock fits it.
///  * Unlock only undoes what BlindsDown did, and only if the device is still in the state BlindsDown
///    left it in.
///  * Volume levels and other settings are never written. Default microphones are written in one case
///    only: turning a default microphone off (here, or its hardware in the helper, see
///    <see cref="NoteDefaultsSettled"/>) makes Windows pick another default, so unlock sets each role back
///    to its pre-lock default, but only while that role still has the default the lock left it with
///    (<see cref="LockJournal.DefaultsWhileLocked"/>), or had none at all because every microphone was off:
///    a default someone picked during the lock is kept. A microphone that is away at unlock gets its default
///    back when it returns.
///  * A default microphone is never turned off while another active microphone stays unprotected
///    (excluded, or its mute failed): Windows would move apps that follow the default onto that
///    microphone. It is only muted then, for the rest of the lock. (One that has only just appeared isn't
///    left open: the default waits until it is muted.) Non-default microphones are turned off before
///    default ones.
///
/// Not thread-safe: the app calls it from one dedicated thread.
/// </summary>
public sealed class LockEngine
{
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TamperWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a turn-off that failed waits before the safety poll tries it again. Windows can take a
    /// couple of seconds to refuse, and the engine thread must not spend them on every poll.</summary>
    private static readonly TimeSpan TurnOffRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly MicRole[] Roles = [MicRole.Console, MicRole.Multimedia, MicRole.Communications];
    private const int TamperThreshold = 5;
    private const string NotConnected = "Not connected";
    private const string WasOnAlready = "It was on already; nothing to turn back on.";

    private readonly IMicBackend _mics;
    private readonly IJournalStore _journalStore;
    private readonly IPendingRestoreStore _pendingStore;
    private readonly IClock _clock;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _guardTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _turnOffRetryAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Changes to the pending-restore file that are not on disk yet, because it couldn't be read or written (another
    /// program held it, the disk was full). Applied to every load until a save succeeds, so nothing owed is lost and
    /// nothing finished is done twice. Memory only: an unlock whose owed restores aren't on disk keeps its journal,
    /// so a restart still finishes it (<see cref="_journalKeptForPending"/>).
    /// </summary>
    private readonly List<Action<List<LockAction>>> _pendingEdits = [];

    private LockJournal? _journal;

    /// <summary>The journal file on disk belongs to a lock that was unlocked, and was kept only because the restores
    /// that unlock still owes could not be saved yet. Deleted once they are.</summary>
    private bool _journalKeptForPending;

    /// <summary>The exclusions in force when this lock started (see <see cref="ExclusionKey"/>). A microphone in the
    /// lock is only let go for an exclusion made since (see <see cref="IsExcluded"/>).</summary>
    private HashSet<string> _exclusionsAtLock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last list of microphones Windows gave during this lock, for when it can't list them right now.
    /// Never one from before the lock: a lock that could not list the microphones could not mute them either.</summary>
    private IReadOnlyList<MicEndpoint>? _lastKnownMics;

    /// <summary>The default microphones as last seen during this lock, so a move Windows forced can be told from
    /// someone's choice (see <see cref="NoteDefaultsMovedByUs"/>). Kept in memory only: after a restart the
    /// journal's record stands in for it.</summary>
    private DefaultMics? _defaultsSeen;
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

    /// <summary>The stronger lock is on for this lock, and Windows hasn't refused it.</summary>
    private bool StrongerLock => _journal is { DisableMicEndpoints: true, EndpointDisableUnavailable: false };

    /// <summary>
    /// Call once at startup. Returns null when there was nothing to recover. Returns a recovery
    /// description when a previous lock is still on disk: either the app exited normally while
    /// locked (the lock simply continues) or it crashed (the UI must ask the user what to do).
    /// After a crash while locked, the guard keeps running meanwhile. After a crash in the middle of
    /// locking, the microphones the lock had already muted are kept muted, and nothing else is changed
    /// until the user chooses. After a crash in the middle of unlocking, nothing is changed until then.
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
        _exclusionsAtLock = ExclusionKeys(_settings()); // Exclusions can't change during a lock: these are the lock's.
        bool clean = journal.Phase == LockPhase.Locked && journal.ExitedCleanlyWhileLocked;

        // A crash right after a microphone was turned off can keep the default that Windows moved away from it
        // out of the journal. It is recorded now, before anything else, so unlock still moves it back.
        bool changed = NoteDefaultsMovedByUs();
        if (journal.ExitedCleanlyWhileLocked)
        {
            journal.ExitedCleanlyWhileLocked = false;
            changed = true;
        }

        if (changed)
        {
            TrySave();
        }

        if (journal.Phase is LockPhase.Locked or LockPhase.Locking)
        {
            Reconcile();
        }

        PublishStatus(force: true);
        return new StartupRecovery(journal.Phase, clean, journal.StartedAt, CountDevicesChanged(journal));
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
        _lastKnownMics = null; // A list from before this lock must not stand in for one it never had (see HardwareToKeepEnabled).
        AudioSnapshot snapshot = TakeSnapshot();

        // Null when the file can't be read right now: nothing is taken from it then, and it is not written.
        List<LockAction>? pending = LoadPending();
        var taken = new List<string>();

        var journal = new LockJournal
        {
            LockId = Guid.NewGuid(),
            Phase = LockPhase.Locking,
            StartedAt = _clock.UtcNow,
            Before = snapshot,
            DisableMicEndpoints = settings.DisableMicsInWindows,
        };

        // Non-default microphones first, defaults last, so apps that follow the default don't hop around.
        foreach (MicEndpoint mic in snapshot.Mics.OrderBy(m => snapshot.Defaults.Contains(m.Id) ? 1 : 0))
        {
            if (!mic.IsUsable || settings.IsExcluded(mic, snapshot.Mics))
            {
                continue;
            }

            (LockAction? owed, DefaultMics? owedDefaults, bool any) = pending is null ? default : TakePendingEntries(pending, mic.Id);
            if (any)
            {
                taken.Add(mic.Id);
            }

            journal.Actions.Add(NewAction(mic, owed, owedDefaults, addedWhileLocked: false));
        }

        // Journal first: if it can't be written, nothing has been changed and BlindsDown stays unlocked. Then the
        // pending list it took entries from (only if it did: a pending file that failed to load must not
        // be overwritten). A crash in between leaves an entry in both; unlock clears such leftovers.
        _journalStore.Save(journal);
        _journal = journal;
        _journalKeptForPending = false; // Replaced by this lock's.
        _exclusionsAtLock = ExclusionKeys(settings);
        if (pending is not null && (taken.Count > 0 || _pendingEdits.Count > 0))
        {
            SavePending(pending, taken.Count == 0 ? null : list => taken.ForEach(id => TakePendingEntries(list, id)));
        }

        PublishStatus();

        foreach (LockAction action in journal.Actions)
        {
            Apply(action);
        }

        TurnOffMicrophones();

        // The defaults the lock settled on (turning default microphones off made Windows pick others). Unlock
        // moves a role back only while it still has this default. Muting never moves a default, so a lock that
        // turned nothing off leaves any change to whoever made it.
        journal.Phase = LockPhase.Locked;
        if (TryGetDefaults() is DefaultMics settled)
        {
            _defaultsSeen = settled;
            if (journal.Actions.Exists(a => a.Kind == ActionKind.DisableMicEndpoint && MayBeChangedByUs(a.Status)))
            {
                journal.DefaultsWhileLocked = SameDefaults(settled, snapshot.Defaults) ? null : settled;
            }
        }

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
        RebaseDefaultsRecord();
        TrySave();
        foreach (LockAction action in _journal.Actions)
        {
            if (action.Kind == ActionKind.MuteMic && action.Status != ActionStatus.Applied)
            {
                Apply(action);
            }
        }

        TurnOffMicrophones();
        _journal.Phase = LockPhase.Locked;
        NoteDefaultsMovedByUs();
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
        NoteDefaultsMovedByUs();
        TrySave();
        PublishStatus();

        // Reverse order. A microphone's turn-off comes after its mute in the journal, so it is turned back on
        // before it is unmuted: a microphone that is off can't be unmuted.
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

        RestoreDefaults(journal, stillPending);

        AudioSnapshot after = TakeSnapshot();
        RestoreReport report = RestoreReport.Build(journal, after);

        // Defaults an earlier lock still owed microphones this lock took over (see NewAction): after this lock's own
        // (the report compares with this lock's start, when an earlier lock's default was owed, not set).
        foreach (LockAction action in journal.Actions.Where(a => a.DefaultsToRestore is not null && !stillPending.Contains(a)))
        {
            HandOnOwedDefaults(action.TargetId, action.DefaultsToRestore!, stillPending);
        }

        // This unlock decides the outcome for every device whose journal entry agrees that it was unmuted
        // before: older pending entries for them are leftovers of an interrupted lock or unlock and, kept,
        // would later unmute a device the user has muted since. An entry the journal disagrees with (this
        // lock first saw the device muted) predates this lock and was not taken over because its file
        // could not be read then: the mute this lock saw was BlindsDown's own, so the entry stays. An older
        // pending turn-on is decided by any entry here: this lock found the device on.
        List<LockAction>? pending = LoadPending();
        foreach (LockAction old in pending?.Where(p => p.DefaultsToRestore is not null && IsDecidedBy(journal, p)) ?? [])
        {
            HandOnOwedDefaults(old.TargetId, old.DefaultsToRestore!, stillPending);
        }

        // Copies: the edit may be applied again to later loads (see SavePending), and must add these as they are now.
        List<LockAction> owed = [.. stillPending.Select(a => a.Copy())];
        void Settle(List<LockAction> list)
        {
            List<LockAction> added = [.. owed.Select(a => a.Copy())];
            foreach (LockAction old in list.FindAll(p => IsDecidedBy(journal, p)))
            {
                list.Remove(old);
                if (old.DefaultsToRestore is { } defaults && PreferredEntry(added, old.TargetId) is { } heir)
                {
                    heir.DefaultsToRestore = MergeDefaults(heir.DefaultsToRestore, defaults);
                }
            }

            list.AddRange(added);
        }

        bool saved = true;
        if (pending is null)
        {
            saved = SavePending(null, Settle);
        }
        else
        {
            int superseded = pending.Count(p => IsDecidedBy(journal, p));
            bool changed = superseded > 0 || owed.Count > 0;
            if (changed)
            {
                Settle(pending);
            }

            if (changed || _pendingEdits.Count > 0)
            {
                saved = SavePending(pending, changed ? Settle : null);
            }
        }

        if (saved || owed.Count == 0)
        {
            try
            {
                _journalStore.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The devices are restored; only the file is left. The next start sees an "interrupted
                // unlock", finds everything already reverted and just tidies up.
                Alert(EngineAlertKind.LockFailed, "lock record cleanup", $"Your microphones are restored, but BlindsDown couldn't delete its lock record ({ex.Message}).", urgent: false);
            }
        }
        else
        {
            // The restores this unlock still owes aren't on disk yet: its journal stays, so a restart still finishes
            // them. Deleted once they are saved.
            _journalKeptForPending = true;
        }

        _journal = null;
        _defaultsSeen = null;
        _guardTimes.Clear();
        _turnOffRetryAt.Clear();
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

    /// <summary>BlindsDown keeps running after all (the exit was cancelled): a crash from now on must offer recovery again.</summary>
    public void ClearCleanExit()
    {
        if (_journal is { ExitedCleanlyWhileLocked: true })
        {
            _journal.ExitedCleanlyWhileLocked = false;
            TrySave();
        }
    }

    /// <summary>A microphone's mute state changed. <paramref name="causedByUs"/> is true for BlindsDown's own changes.</summary>
    public void OnMuteChanged(string endpointId, bool muted, bool causedByUs)
    {
        if (causedByUs || muted || _journal is not { Phase: LockPhase.Locked or LockPhase.Locking } journal)
        {
            return;
        }

        AppSettings settings = _settings();
        IReadOnlyList<MicEndpoint> all = SafeEnumerate();
        MicEndpoint? mic = all.FirstOrDefault(m => SameId(m.Id, endpointId));
        if (mic is not null && IsExcluded(mic, all, settings))
        {
            return; // Excluded during the lock: the user wants to use it. Unlock still restores it.
        }

        LockAction? action = FindAction(endpointId);
        if (journal.Phase == LockPhase.Locking)
        {
            // Waiting for the user's choice after a crash in the middle of a lock: only a microphone the lock had
            // muted is muted again (see Initialize).
            if (action is { Status: ActionStatus.Applied })
            {
                Reapply(action, EngineAlertKind.UnmutedWhileLocked, UnmutedMessage(action));
            }

            return;
        }

        if (action is null)
        {
            if (mic is not null && mic.IsUsable)
            {
                AddWhileLocked(mic, all, settings);
            }

            return;
        }

        if (mic is { State: EndpointState.Disabled } && FindTurnOff(mic.Id) is { Status: ActionStatus.Applied or ActionStatus.Applying } off)
        {
            RecheckTurnedOff(mic, action, off);
            return;
        }

        Reapply(action, EngineAlertKind.UnmutedWhileLocked, UnmutedMessage(action));
    }

    private static string UnmutedMessage(LockAction action) =>
        $"Something unmuted \"{action.DisplayName}\" while locked. BlindsDown muted it again.";

    /// <summary>
    /// An unmute was reported for a microphone this lock has turned off. A microphone that is off can't be unmuted,
    /// so it happened before the turn-off, possibly after the mute was last checked: then the microphone went off
    /// unmuted, and the streams apps already had open would hear it while nothing can mute it. It can't be read while
    /// it is off, so it is turned back on for a moment, muted if it needs to be, and turned off again.
    /// </summary>
    private void RecheckTurnedOff(MicEndpoint mic, LockAction mute, LockAction off)
    {
        // Write-ahead: "may be off by BlindsDown, not verified", so a crash while it is on doesn't look like someone
        // else turning it back on. If that can't be recorded, it stays off: apps can't open it anew meanwhile.
        (ActionStatus Status, string? Note) before = (off.Status, off.Note);
        off.Status = ActionStatus.Applying;
        if (!TrySave())
        {
            (off.Status, off.Note) = before;
            return;
        }

        bool stillOff;
        try
        {
            _mics.SetEndpointEnabled(mic.Id, true);
            stillOff = ReadState(mic.Id) == EndpointState.Disabled;
        }
        catch (DeviceOperationException)
        {
            stillOff = true;
        }

        if (stillOff)
        {
            // It stays off as it was, where apps can't open it anew. Whether it went off unmuted can't be checked, and
            // the safety poll doesn't look at a microphone that is off, so the user is told now.
            (off.Status, off.Note) = before;
            TrySave();
            Alert(
                EngineAlertKind.LockFailed,
                mute.DisplayName,
                $"Something unmuted \"{mute.DisplayName}\" just as BlindsDown turned it off, and Windows did not let BlindsDown turn it back on to mute it. Apps that already had it open may still hear it.",
                urgent: true);
            PublishStatus();
            return;
        }

        if (ReadMute(mic.Id) == false)
        {
            Reapply(mute, EngineAlertKind.UnmutedWhileLocked, UnmutedMessage(mute));
        }

        TurnOff(mic);
        PublishStatus();
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

        AppSettings settings = _settings();
        IReadOnlyList<MicEndpoint> all = SafeEnumerate();
        MicEndpoint? mic = all.FirstOrDefault(m => SameId(m.Id, endpointId));
        if (mic is not null && mic.IsUsable && !IsExcluded(mic, all, settings))
        {
            LockAction? action = FindAction(mic.Id);
            if (action is null)
            {
                AddWhileLocked(mic, all, settings);
            }
            else
            {
                if (action.Status != ActionStatus.Applied || mic.Muted == false)
                {
                    // Not locked yet, or it came back unmuted. (A locked device that merely can't be read right
                    // now, e.g. a Bluetooth hands-free mic that disconnected, keeps its lock until it returns.)
                    bool cameBackUnmuted = action.Status == ActionStatus.Applied;
                    bool remuted = Apply(action);
                    if (cameBackUnmuted && remuted && action.Status == ActionStatus.Applied)
                    {
                        Alert(EngineAlertKind.DeviceRelocked, action.DisplayName, $"\"{action.DisplayName}\" came back unmuted. BlindsDown muted it again.", urgent: false);
                    }
                }

                TurnOffIfDue(mic, all, settings);
            }
        }

        PublishStatus();
    }

    /// <summary>
    /// Safety net, called every few seconds: makes sure every in-scope microphone is still muted
    /// (and, with the stronger lock, off) in case a notification was missed, and finishes deferred restores.
    /// </summary>
    public void Reconcile()
    {
        if (_journal is null)
        {
            ProcessPendingRestores();
            PublishStatus();
            return;
        }

        AppSettings settings = _settings();
        if (_journal.Phase == LockPhase.Locking)
        {
            // Waiting for the user's choice after a crash in the middle of a lock: the microphones the lock had muted
            // are kept muted, nothing else is changed (see Initialize).
            IReadOnlyList<MicEndpoint> all = SafeEnumerate();
            foreach (MicEndpoint mic in all.Where(m => m.Muted == false))
            {
                if (FindAction(mic.Id) is { Status: ActionStatus.Applied } muted && !IsExcluded(mic, all, settings))
                {
                    Reapply(muted, EngineAlertKind.UnmutedWhileLocked, $"\"{muted.DisplayName}\" was unmuted while locked. BlindsDown muted it again.");
                }
            }

            PublishStatus();
            return;
        }

        if (_journal.Phase != LockPhase.Locked)
        {
            PublishStatus();
            return;
        }

        IReadOnlyList<MicEndpoint> mics = SafeEnumerate();
        foreach (MicEndpoint mic in mics)
        {
            if (!mic.IsUsable || IsExcluded(mic, mics, settings))
            {
                continue;
            }

            LockAction? action = FindAction(mic.Id);
            if (action is null)
            {
                AddWhileLocked(mic, mics, settings);
            }
            else if (action.Status != ActionStatus.Applied)
            {
                // Not locked yet (just became readable) or the mute failed: try again. A mute that
                // doesn't stick is a device problem, already reported, not something fighting the lock.
                Apply(action);
            }
            else if (mic.Muted == false)
            {
                Reapply(action, EngineAlertKind.UnmutedWhileLocked, $"\"{action.DisplayName}\" was unmuted while locked. BlindsDown muted it again.");
            }
        }

        if (StrongerLock)
        {
            // Once every microphone is muted: the turn-offs, from a fresh list (the mutes above changed it).
            IReadOnlyList<MicEndpoint> now = SafeEnumerate();
            foreach (MicEndpoint mic in DefaultsLast(now))
            {
                TurnOffIfDue(mic, now, settings);
            }
        }

        // Windows can move a default a moment after a microphone was turned off: recorded as soon as it shows, so a
        // default the user picks later in the lock is never mistaken for one the lock moved.
        if (NoteDefaultsMovedByUs())
        {
            TrySave();
        }

        PublishStatus();
    }

    /// <summary>Forces a fresh status event (e.g. after settings such as exclusions changed).</summary>
    public void RefreshStatus() => PublishStatus(force: true);

    /// <summary>
    /// PnP instance IDs of microphone hardware the helper must NOT disable during this lock: the hardware
    /// behind excluded microphones, and behind default microphones that are kept on so Windows doesn't move
    /// the default to an unprotected microphone (kept on by this lock, or because another active microphone
    /// is unprotected right now). Disabling hardware moves defaults just like turning an endpoint off does,
    /// so the default-microphone rule applies even without the stronger lock. Evaluated when called: the app
    /// calls it right before asking the helper to lock. When Windows can't list the microphones right now, the
    /// last list it gave during this lock stands in, so an excluded microphone keeps its hardware all the same.
    /// Empty when not locked, or when the microphones could not be listed at all during this lock: then nothing
    /// could be muted either, and the hardware disable is the only protection there is.
    /// </summary>
    public IReadOnlyList<string> HardwareToKeepEnabled()
    {
        if (_journal is null)
        {
            return [];
        }

        // Defaults that moved since the last look are sorted out before the helper can move any: a default someone
        // picked a moment ago must never be taken for one the helper moved (see NoteDefaultsSettled).
        if (NoteDefaultsMovedByUs())
        {
            TrySave();
        }

        if ((TryEnumerate() ?? _lastKnownMics) is not { } mics)
        {
            return [];
        }

        AppSettings settings = _settings();
        IEnumerable<MicEndpoint> keep = mics.Where(m => IsExcluded(m, mics, settings) || FindTurnOff(m.Id) is { Status: ActionStatus.Planned });
        if (UnprotectedActiveMic(mics, settings, orNotLockedYet: true) is not null)
        {
            // Unknown defaults: any microphone might be one. Only an active one, though (a default always is): hardware the
            // helper already turned off (its microphones are gone) is never released just because the defaults can't be read.
            DefaultMics? defaults = TryGetDefaults();
            keep = keep.Concat(mics.Where(m => m.State == EndpointState.Active && (defaults?.Contains(m.Id) ?? true)));
        }

        return keep
            .Select(m => m.HardwareInstanceId)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The app calls it right after every part of a lock the helper did, which can disable microphone hardware and
    /// so move defaults too: also when the helper locks late (after a restart, or once it was installed during a
    /// lock). From then on a default that moves away from a microphone that is no longer there counts as moved by
    /// the lock (<see cref="LockJournal.HelperMayMoveDefaults"/>), and unlock puts it back. Windows moves it only
    /// once the disabled hardware's microphone has disappeared, which can be a moment after the helper answers, so
    /// the safety poll keeps following it; a move that already happened is recorded now. Safe however late it is
    /// called: only a move away from the default last seen, which the lock's record still holds, and away from a
    /// microphone that is gone counts (see <see cref="NoteDefaultsMovedByUs"/>), never a default someone picked.
    /// No-op when not locked.
    /// </summary>
    public void NoteDefaultsSettled()
    {
        if (_journal is not { Phase: LockPhase.Locked } journal)
        {
            return;
        }

        bool changed = !journal.HelperMayMoveDefaults;
        journal.HelperMayMoveDefaults = true;
        changed |= NoteDefaultsMovedByUs();
        if (changed)
        {
            TrySave();
        }
    }

    private void AddWhileLocked(MicEndpoint mic, IReadOnlyList<MicEndpoint> all, AppSettings settings)
    {
        // Null when the file can't be read right now: nothing is taken from it then, and it is not written.
        List<LockAction>? pending = LoadPending();
        (LockAction? owed, DefaultMics? owedDefaults, bool took) = pending is null ? default : TakePendingEntries(pending, mic.Id);
        LockAction action = NewAction(mic, owed, owedDefaults, addedWhileLocked: true);

        _journal!.Actions.Add(action);
        if (!TrySave())
        {
            // Not recorded, so it must not be touched: muting it now could never be undone after a crash.
            _journal.Actions.Remove(action);
            PublishStatus();
            return;
        }

        if (pending is not null && (took || _pendingEdits.Count > 0))
        {
            SavePending(pending, took ? list => TakePendingEntries(list, mic.Id) : null);
        }

        Apply(action);
        bool off = TurnOffIfDue(mic, all, settings);
        if (action.Status == ActionStatus.Applied)
        {
            string done = off ? "muted it and turned it off" : "muted it";
            Alert(EngineAlertKind.NewDeviceLocked, mic.Name, $"\"{mic.Name}\" was connected while locked. BlindsDown {done}.", urgent: false);
        }

        PublishStatus();
    }

    /// <summary>Mutes a microphone in the lock if it isn't muted, and verifies it. Returns true when it was found
    /// unmuted and BlindsDown tried to mute it (false when it was muted already or couldn't be read).</summary>
    private bool Apply(LockAction action)
    {
        (ActionStatus Status, string? Note, bool WasAlreadyLocked) before = (action.Status, action.Note, action.WasAlreadyLocked);
        bool touched = MayBeChangedByUs(action.Status);
        bool tried = false;
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
                    // Untouched by this lock so far, so its state right now is its state right before BlindsDown's
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
                        tried = true;
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

        return tried;
    }

    /// <summary>
    /// The guard: mutes a microphone something unmuted while locked, and counts the fight. Only when it is unmuted
    /// right now: one that reads muted again, or can't be read (unplugged, or turned off), is no fight BlindsDown
    /// won, so it says nothing (one that comes back unmuted is muted when it does, see <see cref="OnEndpointChanged"/>).
    /// </summary>
    private void Reapply(LockAction action, EngineAlertKind kind, string message)
    {
        if (!Apply(action))
        {
            PublishStatus();
            return;
        }

        CountFight(
            action,
            kind,
            message,
            times => $"Something keeps unmuting \"{action.DisplayName}\" ({times} times in the last minute). BlindsDown keeps muting it, but check which app is doing this.");
    }

    /// <summary>
    /// One more round of a fight over a device (the guard never gives up): counts it and raises its alert,
    /// which becomes a tampering alert after <see cref="TamperThreshold"/> rounds within <see cref="TamperWindow"/>.
    /// Mutes and turn-offs are counted separately.
    /// </summary>
    private void CountFight(LockAction action, EngineAlertKind kind, string message, Func<int, string> tamperMessage)
    {
        // The count is saved with the alerts below (at most every AlertCooldown per device), not on every
        // round: an app that unmutes the mic whenever BlindsDown mutes it must not cause a stream of
        // flushed journal writes. Losing a few counts in a crash is harmless.
        action.GuardCount++;

        DateTimeOffset now = _clock.UtcNow;
        string key = action.Kind + "|" + action.TargetId;
        if (!_guardTimes.TryGetValue(key, out List<DateTimeOffset>? times))
        {
            times = [];
            _guardTimes[key] = times;
        }

        times.Add(now);
        times.RemoveAll(t => now - t > TamperWindow);
        bool alerted = false;
        if (times.Count >= TamperThreshold)
        {
            alerted = Alert(EngineAlertKind.Tampering, action.DisplayName, tamperMessage(times.Count), urgent: true);
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

    /// <summary>The stronger lock's second step: turns muted physical microphones off, non-default ones first.</summary>
    private void TurnOffMicrophones()
    {
        if (!StrongerLock || TryEnumerate() is not { } mics)
        {
            return;
        }

        AppSettings settings = _settings();
        foreach (MicEndpoint mic in DefaultsLast(mics))
        {
            TurnOffIfDue(mic, mics, settings);
        }
    }

    /// <summary>
    /// The stronger lock for one microphone that is on: turns it off when it is physical, in scope and its mute
    /// is verified (muted first, because turning it off doesn't silence streams that are already open). When
    /// BlindsDown had it off already, something turned it back on (an app, Sound settings, or it came back on
    /// when it was replugged): a fight, reported like an unmute. A turn-off that never took effect is retried
    /// quietly, at most every <see cref="TurnOffRetryInterval"/>. Returns true when the microphone is off.
    /// </summary>
    private bool TurnOffIfDue(MicEndpoint mic, IReadOnlyList<MicEndpoint> all, AppSettings settings)
    {
        if (!StrongerLock
            || mic.IsVirtual
            || !mic.IsUsable
            || IsExcluded(mic, all, settings)
            || FindAction(mic.Id) is not { Status: ActionStatus.Applied })
        {
            return false;
        }

        LockAction? off = FindTurnOff(mic.Id);
        if (off is { Status: ActionStatus.Planned })
        {
            return false; // Kept on for this lock (see KeepOn).
        }

        // Turning a default microphone off makes Windows move apps that follow the default to another one.
        // Unknown defaults: any microphone might be one.
        if (TryGetDefaults()?.Contains(mic.Id) ?? true)
        {
            if (UnprotectedActiveMic(all, settings, orNotLockedYet: false) is { } open)
            {
                KeepOn(mic, open);
                return false;
            }

            if (UnprotectedActiveMic(all, settings, orNotLockedYet: true) is not null)
            {
                // One that appeared a moment ago and is muted as soon as its own notification is handled (several
                // can arrive together): the default waits for it instead of staying on for the whole lock.
                return false;
            }
        }

        if (off is { Status: ActionStatus.ApplyFailed }
            && _turnOffRetryAt.TryGetValue(mic.Id, out DateTimeOffset retryAt)
            && _clock.UtcNow < retryAt)
        {
            return false; // Its mute protects it meanwhile.
        }

        LockAction? turnedBackOn = off is { Status: ActionStatus.Applied } ? off : null;
        TurnOffResult result = TurnOff(mic);
        if (turnedBackOn is not null && result != TurnOffResult.StillUnmuted)
        {
            CountFight(
                turnedBackOn,
                EngineAlertKind.ReEnabledWhileLocked,
                $"Something turned \"{mic.Name}\" back on while locked. BlindsDown turned it off again.",
                times => $"Something keeps turning \"{mic.Name}\" back on ({times} times in the last minute). BlindsDown keeps turning it off, but check which app is doing this.");
        }

        return result == TurnOffResult.Off;
    }

    /// <summary>
    /// Records that this lock keeps a default microphone on, only muted, because another active microphone is
    /// unprotected and Windows would move apps that follow the default onto it. The decision holds for the rest of
    /// the lock, even if that microphone goes away, so the default never moves halfway through a lock. Nothing on
    /// the device changes, so the entry is "Planned": unlock has nothing to undo.
    /// </summary>
    private void KeepOn(MicEndpoint mic, MicEndpoint open)
    {
        LockJournal journal = _journal!;
        LockAction? action = FindTurnOff(mic.Id);
        if (action is null)
        {
            action = new LockAction
            {
                Kind = ActionKind.DisableMicEndpoint,
                TargetId = mic.Id,
                DisplayName = mic.Name,
                AddedWhileLocked = journal.Phase == LockPhase.Locked,
            };
            journal.Actions.Add(action);
        }

        action.Status = ActionStatus.Planned;
        action.Note = $"Kept on so Windows doesn't switch apps to \"{open.Name}\".";
        TrySave();
    }

    /// <summary>
    /// An active microphone this lock leaves open: excluded, or the lock tried and could not mute it. With
    /// <paramref name="orNotLockedYet"/>, also one that has just appeared and has no journal entry yet. Null when
    /// there is none.
    /// </summary>
    private MicEndpoint? UnprotectedActiveMic(IReadOnlyList<MicEndpoint> all, AppSettings settings, bool orNotLockedYet) =>
        all.FirstOrDefault(m => m.State == EndpointState.Active
            && (IsExcluded(m, all, settings)
                || (FindAction(m.Id) is { } action ? action.Status != ActionStatus.Applied : orNotLockedYet)));

    private enum TurnOffResult
    {
        Off,
        NotOff,

        /// <summary>Not turned off: it was found unmuted and would not stay muted.</summary>
        StillUnmuted,
    }

    /// <summary>
    /// Turns a muted microphone off in Windows and checks that it is off. Like the mute, it is written to the
    /// journal as "Applying" first, so a crash right after the change still gets it undone. Right before the
    /// change its mute is read again: something may have unmuted it since its mute was verified (that notification
    /// waits until the engine is done), and once it is off it can't be muted, while the streams apps already have
    /// open keep hearing it. Then it is muted again first (a fight, reported as such), and stays on if that doesn't
    /// hold.
    /// </summary>
    private TurnOffResult TurnOff(MicEndpoint mic)
    {
        LockJournal journal = _journal!;
        LockAction? action = FindTurnOff(mic.Id);
        bool added = action is null;
        action ??= new LockAction
        {
            Kind = ActionKind.DisableMicEndpoint,
            TargetId = mic.Id,
            DisplayName = mic.Name,
            AddedWhileLocked = journal.Phase == LockPhase.Locked,
        };

        (ActionStatus Status, string? Note) before = (action.Status, action.Note);
        if (!MayBeChangedByUs(action.Status))
        {
            action.Status = ActionStatus.Applying;
            if (added)
            {
                journal.Actions.Add(action);
            }

            if (!TrySave())
            {
                // Not recorded, so it must not be changed. It stays on, still protected by its mute.
                if (added)
                {
                    journal.Actions.Remove(action);
                }
                else
                {
                    (action.Status, action.Note) = before;
                }

                return TurnOffResult.NotOff;
            }
        }

        // Catches up with default changes since the last look while this microphone is still on, so a default
        // someone picked meanwhile is seen as their choice, not as this turn-off's doing.
        bool defaultsMoved = NoteDefaultsMovedByUs();
        if (!MutedRightNow(mic.Id))
        {
            // It stays on, where the guard can keep muting it. Nothing was changed, so the write-ahead goes.
            bool wroteAhead = !MayBeChangedByUs(before.Status);
            if (wroteAhead && added)
            {
                journal.Actions.Remove(action);
            }
            else if (wroteAhead)
            {
                (action.Status, action.Note) = before;
            }

            if (wroteAhead || defaultsMoved)
            {
                TrySave();
            }

            return TurnOffResult.StillUnmuted;
        }

        bool removed = false;
        DefaultMics? defaultsBefore = TryGetDefaults();
        try
        {
            _mics.SetEndpointEnabled(mic.Id, false);
            bool off = ReadState(mic.Id) == EndpointState.Disabled;
            action.Status = off ? ActionStatus.Applied : ActionStatus.ApplyFailed;
            action.Note = off ? null : "Windows did not turn it off";
        }
        catch (DeviceOperationException ex) when (ex.ErrorKind is DeviceErrorKind.AccessDenied or DeviceErrorKind.NotSupported)
        {
            // Windows doesn't allow it here, so nothing changed, and asking again for every other microphone
            // would fail the same way: from now on this lock only mutes (once said, never repeated).
            journal.EndpointDisableUnavailable = true;
            if (added)
            {
                journal.Actions.Remove(action);
                removed = true;
            }
            else
            {
                action.Status = ActionStatus.Reverted;
                action.Note = WasOnAlready;
            }

            string why = ex.ErrorKind == DeviceErrorKind.AccessDenied
                ? "Windows wants administrator rights to turn microphones off here"
                : "This version of Windows doesn't support turning microphones off";
            Alert(EngineAlertKind.StrongerLockUnavailable, "Microphones", $"{why}, so BlindsDown only muted them. They are still locked.", urgent: false);
        }
        catch (DeviceOperationException ex)
        {
            // Its mute still protects it; the safety poll tries again.
            action.Status = ActionStatus.ApplyFailed;
            action.Note = ex.Message;
        }

        if (!removed && action.Status == ActionStatus.ApplyFailed)
        {
            _turnOffRetryAt[mic.Id] = _clock.UtcNow + TurnOffRetryInterval;
        }
        else
        {
            _turnOffRetryAt.Remove(mic.Id);
        }

        // Saved only on a change: the safety poll retries a turn-off that doesn't stick, and that must not
        // cause a stream of disk writes.
        defaultsMoved |= NoteDefaultsMovedFrom(mic.Id, defaultsBefore);
        defaultsMoved |= NoteDefaultsMovedByUs();
        if (removed || defaultsMoved || (action.Status, action.Note) != before)
        {
            TrySave();
        }

        return !removed && action.Status == ActionStatus.Applied ? TurnOffResult.Off : TurnOffResult.NotOff;
    }

    /// <summary>
    /// Right before a microphone is turned off: whether it is muted now, muting it again first when something
    /// unmuted it (a fight, counted and reported like any other). One that can't be read can't be opened by apps
    /// either, so its verified mute stands.
    /// </summary>
    private bool MutedRightNow(string endpointId)
    {
        if (ReadMute(endpointId) != false)
        {
            return true;
        }

        if (FindAction(endpointId) is not { } mute)
        {
            return false;
        }

        Reapply(mute, EngineAlertKind.UnmutedWhileLocked, UnmutedMessage(mute));
        return mute.Status == ActionStatus.Applied && ReadMute(endpointId) != false;
    }

    /// <summary>A microphone's mute, or null when it can't be read right now.</summary>
    private bool? ReadMute(string endpointId)
    {
        try
        {
            return _mics.GetMute(endpointId);
        }
        catch (DeviceOperationException)
        {
            return null;
        }
    }

    private void Revert(LockAction action)
    {
        if (action.Kind == ActionKind.DisableMicEndpoint)
        {
            TurnOn(action);
        }
        else
        {
            Unmute(action);
        }
    }

    private void Unmute(LockAction action)
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

        bool mayBeMutedByUs = MayBeChangedByUs(action.Status);
        try
        {
            bool? current = _mics.GetMute(action.TargetId);
            if (current is null)
            {
                // Can't reach it now. If BlindsDown may have muted it, finish the job when it comes back.
                action.Status = mayBeMutedByUs ? ActionStatus.RevertPending : ActionStatus.Reverted;
                return;
            }

            if (current == false || !mayBeMutedByUs)
            {
                // Already unmuted, or BlindsDown never changed it: nothing to undo.
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

    /// <summary>
    /// Turns a microphone this lock turned off back on, if it is still off. A note on a reverted turn-off
    /// means there was nothing to turn back on, which the restore report words differently.
    /// </summary>
    private void TurnOn(LockAction action)
    {
        if (action.Status == ActionStatus.Reverted)
        {
            return; // Done in an earlier, interrupted unlock. (A failed turn-on is simply tried again.)
        }

        if (!MayBeChangedByUs(action.Status))
        {
            action.Status = ActionStatus.Reverted;
            action.Note = WasOnAlready;
            return;
        }

        try
        {
            EndpointState? state = _mics.EnumerateMics().FirstOrDefault(m => SameId(m.Id, action.TargetId))?.State;
            if (state is null or EndpointState.NotPresent)
            {
                // Away. It stays off while it is gone (Windows keeps the setting) and is turned back on when it returns.
                action.Status = ActionStatus.RevertPending;
                return;
            }

            if (state != EndpointState.Disabled)
            {
                // Turned back on by someone else, or the turn-off never took effect.
                action.Status = ActionStatus.Reverted;
                action.Note = WasOnAlready;
                return;
            }

            _mics.SetEndpointEnabled(action.TargetId, true);
            EndpointState? after = ReadState(action.TargetId);
            if (after == EndpointState.NotPresent)
            {
                action.Status = ActionStatus.RevertPending; // Unplugged just now; checked again when it returns.
                return;
            }

            bool on = after is EndpointState.Active or EndpointState.Unplugged;
            action.Status = on ? ActionStatus.Reverted : ActionStatus.RevertFailed;
            action.Note = on ? null : "Windows did not turn it back on";
        }
        catch (DeviceOperationException ex)
        {
            action.Status = ActionStatus.RevertFailed;
            action.Note = ex.Message;
        }
    }

    /// <summary>
    /// After undoing the devices: sets each default-microphone role back to its pre-lock default when the lock
    /// moved it and nobody picked another default since: the role still has the default the lock left it with,
    /// or it had none at all while locked (every microphone was off), so the one it has now is the microphone
    /// Windows happened to give it as they came back on. A role whose pre-lock default is away is handed to that
    /// microphone's pending restore. The restore report, built right after from a fresh reading, shows any role
    /// that still differs.
    /// </summary>
    private void RestoreDefaults(LockJournal journal, List<LockAction> stillPending)
    {
        if (journal is not { DefaultsWhileLocked: { } whileLocked, Before: { } before } || TryGetDefaults() is not { } now)
        {
            return;
        }

        IReadOnlyList<MicEndpoint> mics = SafeEnumerate();
        foreach (MicRole role in Roles)
        {
            string? original = Role(before.Defaults, role);
            string? left = Role(whileLocked, role);
            string? current = Role(now, role);
            if (original is null || SameId(original, left) || SameId(original, current))
            {
                continue; // Not moved by the lock, or back already.
            }

            if (left is not null && !SameId(current, left))
            {
                continue; // Someone picked another default since.
            }

            if (mics.FirstOrDefault(m => SameId(m.Id, original)) is { State: EndpointState.Active })
            {
                TrySetDefault(original, role);
                continue;
            }

            // Away: made the default again when it comes back, if this role still has the same default then.
            LockAction? owed = PreferredEntry(stillPending, original);
            if (owed is not null && current is not null)
            {
                owed.DefaultsToRestore = WithRole(owed.DefaultsToRestore ?? DefaultMics.None, role, current);
            }
        }
    }

    private void TrySetDefault(string endpointId, MicRole role)
    {
        try
        {
            _mics.SetDefaultMic(endpointId, role);
        }
        catch (DeviceOperationException)
        {
            // Nothing else to try: the restore report (or the "restored" alert) is built from a fresh reading.
        }
    }

    /// <summary>
    /// Records in <see cref="LockJournal.DefaultsWhileLocked"/> every role that had <paramref name="endpointId"/>
    /// as its default right before this lock turned it off (<paramref name="before"/>) and has another one now:
    /// Windows moved it because of BlindsDown. Exact even when the default had come back to that microphone
    /// during the lock. Returns true when the record changed (the caller saves it).
    /// </summary>
    private bool NoteDefaultsMovedFrom(string endpointId, DefaultMics? before)
    {
        if (_journal is not { Before: { } snapshot } journal || before is null || TryGetDefaults() is not { } now)
        {
            return false;
        }

        DefaultMics known = journal.DefaultsWhileLocked ?? snapshot.Defaults;
        DefaultMics updated = known;
        foreach (MicRole role in Roles)
        {
            if (SameId(Role(before, role), endpointId) && !SameId(Role(now, role), endpointId))
            {
                updated = WithRole(updated, role, Role(now, role));
            }
        }

        if (updated == known)
        {
            return false;
        }

        journal.DefaultsWhileLocked = updated;
        return true;
    }

    /// <summary>
    /// Follows Windows when it had to give a role another default because the default the lock left it with
    /// (<see cref="LockJournal.DefaultsWhileLocked"/>) stopped being active, and that is the lock's doing: the lock
    /// may have turned that microphone off (a microphone that is off can't be a default), the helper may have
    /// disabled its hardware (it disappeared after <see cref="NoteDefaultsSettled"/>), or the lock had moved the
    /// role already and still owes it its pre-lock default, wherever Windows takes it next. Unlock then moves the
    /// role back. This catches what an exact record missed: a crash right after a turn-off, or Windows moving the
    /// default a moment later.
    ///
    /// Only a move from the default seen last time counts, and only away from a microphone that is no longer
    /// active: a role moved away from a microphone that is still on was someone's choice (a microphone that is on
    /// stays the default until someone picks another). That choice is kept, also when the microphone it replaced
    /// goes away later. Called after every turn-off, by the safety poll, and at startup and unlock. Returns true
    /// when the record changed (the caller saves it).
    /// </summary>
    private bool NoteDefaultsMovedByUs()
    {
        if (_journal is not { Before: { } before } journal || TryGetDefaults() is not { } now)
        {
            return false;
        }

        if (journal is { DefaultsWhileLocked: null, HelperMayMoveDefaults: false } && !journal.Actions.Exists(a => a.Kind == ActionKind.DisableMicEndpoint))
        {
            _defaultsSeen = now; // The lock can't have moved anything yet: whatever changed was someone's choice.
            return false;
        }

        if (TryEnumerate() is not { } mics)
        {
            return false; // Looked at again on the next call, against the same last-seen defaults.
        }

        DefaultMics record = journal.DefaultsWhileLocked ?? before.Defaults;
        DefaultMics seen = _defaultsSeen ?? record;
        _defaultsSeen = now;
        DefaultMics updated = record;
        foreach (MicRole role in Roles)
        {
            string? was = Role(seen, role);
            MicEndpoint? wasMic = was is null ? null : mics.FirstOrDefault(m => SameId(m.Id, was));
            if (was is not null
                && SameId(was, Role(record, role))
                && !SameId(was, Role(now, role))
                && wasMic is not { State: EndpointState.Active }
                && ((FindTurnOff(was) is { } off && MayBeChangedByUs(off.Status))
                    || (journal.HelperMayMoveDefaults && wasMic is null or { State: EndpointState.NotPresent })
                    || !SameId(was, Role(before.Defaults, role))))
            {
                updated = WithRole(updated, role, Role(now, role));
            }
        }

        if (updated == record)
        {
            return false;
        }

        journal.DefaultsWhileLocked = updated;
        return true;
    }

    /// <summary>
    /// Before a lock is resumed: an interrupted unlock may have set some defaults back already. Those roles are as
    /// before the lock again, so they are recorded as unmoved, and a turn-off that moves them now is recorded afresh.
    /// </summary>
    private void RebaseDefaultsRecord()
    {
        if (_journal is not { DefaultsWhileLocked: { } record, Before: { } before } journal || TryGetDefaults() is not { } now)
        {
            return;
        }

        DefaultMics rebased = record;
        foreach (MicRole role in Roles)
        {
            if (SameId(Role(now, role), Role(before.Defaults, role)))
            {
                rebased = WithRole(rebased, role, Role(before.Defaults, role));
            }
        }

        _defaultsSeen = now;
        journal.DefaultsWhileLocked = SameDefaults(rebased, before.Defaults) ? null : rebased;
    }

    /// <summary>
    /// Finishes the restores of devices that were away when the last lock ended, once they are back: turned
    /// back on first, then unmuted, then made the default again where the lock had moved the default away.
    /// </summary>
    private void ProcessPendingRestores()
    {
        List<LockAction>? pending = LoadPending();
        if (pending is null)
        {
            return; // Can't be read right now: looked at again on the next pass.
        }

        // Finished entries by what they were as loaded, so a save that fails can still drop them from later loads.
        var finished = new List<string>();
        foreach (string id in pending.Select(p => p.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var done = new List<LockAction>();
            foreach (LockAction action in pending.Where(p => SameId(p.TargetId, id)).OrderBy(p => p.Kind == ActionKind.DisableMicEndpoint ? 0 : 1).ToList())
            {
                // A microphone that is off is present but not usable: it can be turned on, not unmuted.
                MicEndpoint? mic = FindMic(id);
                bool back = action.Kind == ActionKind.DisableMicEndpoint
                    ? mic is not null && mic.State != EndpointState.NotPresent
                    : mic is { IsUsable: true };
                if (!back)
                {
                    break;
                }

                string key = PendingKey(action);
                Revert(action);
                if (action.Status == ActionStatus.RevertPending)
                {
                    break;
                }

                done.Add(action);
                finished.Add(key);
            }

            if (done.Count > 0)
            {
                pending.RemoveAll(done.Contains);
                FinishDeferredRestore(id, done);
            }
        }

        if (finished.Count > 0 || _pendingEdits.Count > 0)
        {
            SavePending(pending, finished.Count == 0 ? null : list => RemoveFinished(list, finished));
        }
    }

    /// <summary>What identifies a pending entry across loads of the file.</summary>
    private static string PendingKey(LockAction action) =>
        $"{action.Kind}|{action.TargetId.ToUpperInvariant()}|{action.WasAlreadyLocked}|{action.Status}";

    /// <summary>Drops one entry per key (see <see cref="PendingKey"/>): restores that are finished.</summary>
    private static void RemoveFinished(List<LockAction> pending, List<string> finished)
    {
        foreach (string key in finished)
        {
            int index = pending.FindIndex(p => PendingKey(p) == key);
            if (index >= 0)
            {
                pending.RemoveAt(index);
            }
        }
    }

    private void FinishDeferredRestore(string endpointId, List<LockAction> done)
    {
        // Its default back first, even when the unmute failed: these entries leave the pending list now, and the
        // default doesn't depend on the mute. (A microphone that is still off can't take it and is skipped.)
        RestoreOwedDefaults(endpointId, done.Select(a => a.DefaultsToRestore).Aggregate((DefaultMics?)null, MergeDefaults));
        string name = done[0].DisplayName;
        LockAction? failed = done.Find(a => a.Status != ActionStatus.Reverted);
        if (failed is not null)
        {
            string fix = failed.Kind == ActionKind.DisableMicEndpoint ? "Turn it on" : "Unmute it";
            Alert(EngineAlertKind.LockFailed, name, $"Could not restore \"{name}\": {failed.Note?.TrimEnd('.')}. {fix} in Windows Sound settings.", urgent: true);
            return;
        }

        bool turnedOn = done.Exists(a => a.Kind == ActionKind.DisableMicEndpoint && a.Note is null);
        bool unmuted = done.Exists(a => a.Kind == ActionKind.MuteMic);
        string? what = (turnedOn, unmuted) switch
        {
            (true, true) => "turned back on and unmuted",
            (true, false) => "turned back on",
            (false, true) => "unmuted",
            _ => null,
        };
        if (what is not null)
        {
            Alert(EngineAlertKind.DeferredRestoreDone, name, $"\"{name}\" is back and was restored ({what}, as before the lock).", urgent: false);
        }
    }

    /// <summary>Makes a returned microphone the default again for every role its lock moved away from it
    /// (<see cref="LockAction.DefaultsToRestore"/>), unless the role's default changed since that unlock.</summary>
    private void RestoreOwedDefaults(string endpointId, DefaultMics? owed)
    {
        if (owed is null
            || FindMic(endpointId) is not { State: EndpointState.Active }
            || TryGetDefaults() is not { } now)
        {
            return;
        }

        foreach (MicRole role in Roles)
        {
            string? expected = Role(owed, role);
            if (expected is not null && SameId(Role(now, role), expected))
            {
                TrySetDefault(endpointId, role);
            }
        }
    }

    /// <summary>
    /// At the unlock of a lock that took over a microphone an earlier lock still owed default-microphone roles (see
    /// <see cref="NewAction"/>): handed on to the microphone's pending restore when it is away again, given back now
    /// otherwise (each role only while it still has the default it had when that was recorded).
    /// </summary>
    private void HandOnOwedDefaults(string endpointId, DefaultMics owed, List<LockAction> stillPending)
    {
        if (PreferredEntry(stillPending, endpointId) is { } heir)
        {
            heir.DefaultsToRestore = MergeDefaults(heir.DefaultsToRestore, owed);
            return;
        }

        RestoreOwedDefaults(endpointId, owed);
    }

    /// <summary>The entry of a device that carries its owed default roles: its unmute if it has one, else its turn-on.</summary>
    private static LockAction? PreferredEntry(IEnumerable<LockAction> actions, string endpointId) =>
        actions
            .Where(p => SameId(p.TargetId, endpointId))
            .OrderBy(p => p.Kind == ActionKind.MuteMic ? 0 : 1)
            .FirstOrDefault();

    /// <summary>Owed default roles from two records; for a role in both, <paramref name="newer"/> wins.</summary>
    private static DefaultMics? MergeDefaults(DefaultMics? newer, DefaultMics? older) =>
        newer is null || older is null
            ? newer ?? older
            : new DefaultMics(newer.Console ?? older.Console, newer.Multimedia ?? older.Multimedia, newer.Communications ?? older.Communications);

    /// <summary>
    /// Removes what an earlier lock still owes a device that is usable right now: returns its pending unmute (if
    /// any) and the default-microphone roles owed to it, and drops its pending turn-on (a usable device is on
    /// already). <c>Any</c> is true when an entry was removed.
    /// </summary>
    private static (LockAction? Unmute, DefaultMics? Defaults, bool Any) TakePendingEntries(List<LockAction> pending, string endpointId)
    {
        LockAction? entry = pending.Find(p => p.Kind == ActionKind.MuteMic && SameId(p.TargetId, endpointId));
        List<LockAction> taken = pending.FindAll(p => SameId(p.TargetId, endpointId) && (ReferenceEquals(p, entry) || p.Kind == ActionKind.DisableMicEndpoint));
        pending.RemoveAll(taken.Contains);
        DefaultMics? owed = taken
            .OrderBy(p => p.Kind == ActionKind.MuteMic ? 0 : 1)
            .Select(p => p.DefaultsToRestore)
            .Aggregate((DefaultMics?)null, MergeDefaults);
        return (entry, owed, taken.Count > 0);
    }

    /// <summary>
    /// Whether an older pending restore is decided by this unlock (see <see cref="Unlock"/>). A pending turn-on
    /// is decided by any entry for the device: this lock found it usable, so it was on.
    /// </summary>
    private static bool IsDecidedBy(LockJournal journal, LockAction pending) =>
        journal.Actions.Exists(a => SameId(a.TargetId, pending.TargetId)
            && (pending.Kind == ActionKind.DisableMicEndpoint || (a.Kind == ActionKind.MuteMic && !a.WasAlreadyLocked)));

    /// <summary>
    /// The journal entry for a device about to be locked. A device an earlier lock still owes a restore
    /// is still muted by BlindsDown: its true original state comes from that entry, and it starts as
    /// "may already be muted by BlindsDown" so that unlock (or a crash) can never leave it muted. Default roles
    /// that lock still owes it (<paramref name="owedDefaults"/>) go with it and are settled at this lock's
    /// unlock: this lock's own snapshot already shows the default that took over meanwhile.
    /// </summary>
    private static LockAction NewAction(MicEndpoint mic, LockAction? owedRestore, DefaultMics? owedDefaults, bool addedWhileLocked) => new()
    {
        Kind = ActionKind.MuteMic,
        TargetId = mic.Id,
        DisplayName = mic.Name,
        WasAlreadyLocked = owedRestore?.WasAlreadyLocked ?? mic.Muted == true,
        Status = owedRestore is null ? ActionStatus.Planned : ActionStatus.Applying,
        AddedWhileLocked = addedWhileLocked,
        DefaultsToRestore = owedDefaults,
    };

    /// <summary>
    /// False only when BlindsDown certainly has not left its own change on the device: not touched in this
    /// lock yet (Planned) or already undone (Reverted). Anything else must be undone at unlock.
    /// </summary>
    private static bool MayBeChangedByUs(ActionStatus status) =>
        status is not (ActionStatus.Planned or ActionStatus.Reverted);

    private static int CountDevicesChanged(LockJournal journal) =>
        journal.Actions
            .Where(a => !a.WasAlreadyLocked && MayBeChangedByUs(a.Status))
            .Select(a => a.TargetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private AudioSnapshot TakeSnapshot() => new(SafeEnumerate(), TryGetDefaults() ?? DefaultMics.None, _clock.UtcNow);

    private DefaultMics? TryGetDefaults()
    {
        try
        {
            return _mics.GetDefaultMics();
        }
        catch (DeviceOperationException)
        {
            return null;
        }
    }

    /// <summary>Every microphone, or null when Windows could not list them.</summary>
    private IReadOnlyList<MicEndpoint>? TryEnumerate()
    {
        try
        {
            return _lastKnownMics = _mics.EnumerateMics();
        }
        catch (DeviceOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a microphone is left out of this lock. Once a microphone is in the lock it stays in, unless an
    /// exclusion made after the lock started fits it: an exclusion that fits it now only because the device it was
    /// made for went away (an identical twin, see <see cref="ExclusionRules"/>) must not open it halfway through.
    /// </summary>
    private bool IsExcluded(MicEndpoint mic, IReadOnlyList<MicEndpoint> all, AppSettings settings)
    {
        if (FindAction(mic.Id) is null)
        {
            return settings.IsExcluded(mic, all);
        }

        List<DeviceExclusion> made = settings.ExcludedDevices.Where(e => e is not null && !_exclusionsAtLock.Contains(ExclusionKey(e))).ToList();
        return AppSettings.IsExcluded(made, mic, all);
    }

    private static HashSet<string> ExclusionKeys(AppSettings settings) =>
        new(settings.ExcludedDevices.Where(e => e is not null).Select(ExclusionKey), StringComparer.OrdinalIgnoreCase);

    private static string ExclusionKey(DeviceExclusion exclusion) => exclusion.StableKey + "\n" + exclusion.LastKnownId;

    /// <summary>
    /// The pending restores: the file's entries with every change not on disk yet applied (see
    /// <see cref="_pendingEdits"/>). Null when the file can't be read right now: it still owes restores, so it must
    /// not be overwritten.
    /// </summary>
    private List<LockAction>? LoadPending()
    {
        List<LockAction> pending;
        try
        {
            pending = _pendingStore.Load();
        }
        catch (PendingRestoreUnreadableException)
        {
            return null;
        }

        foreach (Action<List<LockAction>> edit in _pendingEdits)
        {
            edit(pending);
        }

        return pending;
    }

    /// <summary>
    /// Writes the pending restores, <paramref name="pending"/> (from <see cref="LoadPending"/>, null when it couldn't
    /// be read) with <paramref name="edit"/> applied. When that fails, the edit is kept and applied to every later
    /// load until a save succeeds (see <see cref="_pendingEdits"/>), and the user is told once. Returns true when
    /// the file is up to date.
    /// </summary>
    private bool SavePending(List<LockAction>? pending, Action<List<LockAction>>? edit)
    {
        string problem;
        if (pending is null)
        {
            problem = "can't open its list of microphones to restore later right now (another program may be using it)";
        }
        else
        {
            try
            {
                _pendingStore.Save(pending);
                _pendingEdits.Clear();
                DeleteKeptJournal();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"can't save its list of microphones to restore later ({ex.Message})";
            }
        }

        if (edit is not null)
        {
            if (_pendingEdits.Count == 0)
            {
                Alert(EngineAlertKind.LockFailed, "restore list", $"BlindsDown {problem}. It keeps the list while it runs and tries again.", urgent: false);
            }

            _pendingEdits.Add(edit);
        }

        return false;
    }

    /// <summary>Deletes the journal an unlock kept for its owed restores (see <see cref="_journalKeptForPending"/>),
    /// once they are saved and no other lock has started since.</summary>
    private void DeleteKeptJournal()
    {
        if (!_journalKeptForPending || _journal is not null)
        {
            return;
        }

        try
        {
            _journalStore.Delete();
            _journalKeptForPending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again after the next save. A restart before that finds an unlock with nothing left to do.
        }
    }

    private IReadOnlyList<MicEndpoint> SafeEnumerate() => TryEnumerate() ?? [];

    private MicEndpoint? FindMic(string endpointId) =>
        SafeEnumerate().FirstOrDefault(m => SameId(m.Id, endpointId));

    /// <summary>An endpoint's state, or null when Windows can't list the microphones or doesn't list this one.</summary>
    private EndpointState? ReadState(string endpointId) =>
        TryEnumerate()?.FirstOrDefault(m => SameId(m.Id, endpointId))?.State;

    /// <summary>Default microphones last, so a default is only turned off after the ones Windows could move it to.</summary>
    private IEnumerable<MicEndpoint> DefaultsLast(IReadOnlyList<MicEndpoint> mics)
    {
        DefaultMics defaults = TryGetDefaults() ?? DefaultMics.None;
        return mics.OrderBy(m => defaults.Contains(m.Id) ? 1 : 0);
    }

    private LockAction? FindAction(string endpointId) =>
        _journal?.Actions.Find(a => a.Kind == ActionKind.MuteMic && SameId(a.TargetId, endpointId));

    private LockAction? FindTurnOff(string endpointId) =>
        _journal?.Actions.Find(a => a.Kind == ActionKind.DisableMicEndpoint && SameId(a.TargetId, endpointId));

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameDefaults(DefaultMics a, DefaultMics b) => Roles.All(r => SameId(Role(a, r), Role(b, r)));

    private static string? Role(DefaultMics defaults, MicRole role) => role switch
    {
        MicRole.Console => defaults.Console,
        MicRole.Multimedia => defaults.Multimedia,
        _ => defaults.Communications,
    };

    private static DefaultMics WithRole(DefaultMics defaults, MicRole role, string? endpointId) => role switch
    {
        MicRole.Console => defaults with { Console = endpointId },
        MicRole.Multimedia => defaults with { Multimedia = endpointId },
        _ => defaults with { Communications = endpointId },
    };

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
        if (kind is EngineAlertKind.UnmutedWhileLocked or EngineAlertKind.ReEnabledWhileLocked or EngineAlertKind.Tampering or EngineAlertKind.LockFailed
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
            LockAction? off = FindTurnOff(mic.Id);
            bool relevant = mic.IsUsable || mic.State == EndpointState.Disabled || action is not null;
            if (!relevant)
            {
                continue;
            }

            DeviceProtection protection;
            string? problem = null;
            string? detail = null;
            if (mic.State == EndpointState.Disabled)
            {
                // Off in Windows: by this lock (apps can't open it), or by someone else (left alone).
                protection = journal is not null && off is not null && MayBeChangedByUs(off.Status)
                    ? DeviceProtection.EndpointDisabled
                    : DeviceProtection.DisabledByUser;
            }
            else if (IsExcluded(mic, mics!, settings))
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
                detail = WhyStillOn(off);
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

            devices.Add(new DeviceLockInfo(mic.Id, mic.Name, mic.StableKey, DeviceKind.Microphone, mic.IsVirtual, mic.HasHardwareMute, protection, problem, Detail: detail));
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

    /// <summary>During a stronger lock: why a muted microphone is not also off, when that is worth saying. Not a
    /// problem: the mute protects it.</summary>
    private string? WhyStillOn(LockAction? off)
    {
        if (_journal is not { Phase: LockPhase.Locked })
        {
            return null;
        }

        return off switch
        {
            { Status: ActionStatus.Planned } => off.Note, // Kept on so the default doesn't move.
            { Status: ActionStatus.ApplyFailed } => $"Not turned off in Windows: {off.Note?.TrimEnd('.')}.",
            _ => null,
        };
    }

    private static string Signature(LockStatusInfo status) =>
        status.State + ";" + string.Join(";", status.Devices.Select(d => $"{d.Id}={d.Protection}:{d.Problem}:{d.Detail}"));
}
