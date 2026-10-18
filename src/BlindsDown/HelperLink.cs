using System.Globalization;
using System.Text;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;
using BlindsDown.Ui;

namespace BlindsDown;

/// <summary>
/// The tray app's connection to the optional camera helper: whether it is installed and answering, its lock
/// status and alerts (polled every 2 s while locked or while Settings is open, otherwise every 15 s), Lock and
/// Unlock, and the elevated install, update and removal. UI thread only; the async methods resume there.
/// </summary>
/// <remarks>
/// Any program running as a signed-in user may ask the helper to unlock, so the helper keeps a log of lock changes
/// (on disk, so it survives a restart of the helper), each with a fingerprint of the ID of the request that made it
/// (<see cref="HelperChange"/>). Every Lock and Unlock this app (or its --restore, or a setup run it starts) sends
/// carries a new random ID of its own; a change whose fingerprint isn't one of this app's, or doesn't fit the command
/// it was sent with, is another program's (<see cref="ChangesReceived"/>). The helper never sends the IDs themselves
/// and refuses an ID it has seen, so another program can't pass its change off as this app's. The position in that
/// log, the fingerprints of this user's own requests and whether the helper holds this user's lock are kept in a
/// small file, so a change made while BlindsDown wasn't running is still noticed at the next start.
/// </remarks>
internal sealed class HelperLink : IDisposable
{
    private const int FastPollMilliseconds = 2000;
    private const int SlowPollMilliseconds = 15000;
    private const int OwnRequestsKept = 64;
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Turning several devices off or on can take a while (each one is verified), and the helper may first be busy with a
    /// previous request: it gives up on a queued request after two minutes and says so. Waiting longer than that means
    /// its own answer ("busy", or the result) always arrives here, instead of the request running after this app gave up.
    /// </summary>
    private static readonly TimeSpan ChangeTimeout = HelperTimeouts.ChangeTimeout;

    /// <summary>How long a Lock or Unlock this app gave up on may still finish in the helper.</summary>
    private static readonly TimeSpan LateChangeWindow = TimeSpan.FromMinutes(3);

    private readonly HelperClient _client;
    private readonly Func<HelperInstallInfo> _query;
    private readonly Func<ElevatedTask, IWin32Window?, string?, Task<int?>> _launch;
    private readonly string? _statePath;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = SlowPollMilliseconds };
    private readonly List<OwnRequest> _ownRequests = [];

    /// <summary>The helper holds this user's lock, as far as this app knows (see <see cref="HeldOwnLock"/>).</summary>
    private bool _held;

    /// <summary>Fingerprints of a finished setup run's IDs: forgotten once the next answer has been read (its changes
    /// are in by then), as the IDs were on that run's command line.</summary>
    private readonly HashSet<string> _setupDigestsToForget = new(StringComparer.Ordinal);

    /// <summary>The newest helper alert or change seen; -1 until the first answer, which then only sets it.</summary>
    private long _lastEventId = -1;

    /// <summary>The run of the helper <see cref="_lastEventId"/> came from (<see cref="HelperResponse.RunId"/>; 0: not
    /// known), sent back with it so the helper can tell a position from one of its earlier runs exactly.</summary>
    private long _runId;

    /// <summary>The position came from the file (an earlier run): the first answer replays the lock changes made
    /// meanwhile, but not old alerts.</summary>
    private bool _resumedFromFile;
    private bool _statusKnown;
    private DateTimeOffset _ownChangeUntil = DateTimeOffset.MinValue;
    private bool _polling;
    private bool _changing;
    private bool _disposed;

    /// <param name="statePath">Where the log position and this user's request IDs are kept (null in tests: memory only).</param>
    /// <param name="client">The pipe client (tests pass one to a test pipe).</param>
    /// <param name="query">Asks Windows whether the service is installed (tests pass a fake).</param>
    /// <param name="launch">Starts an elevated setup run and returns its exit code (tests pass a fake).</param>
    public HelperLink(
        string? statePath,
        HelperClient? client = null,
        Func<HelperInstallInfo>? query = null,
        Func<ElevatedTask, IWin32Window?, string?, Task<int?>>? launch = null)
    {
        _statePath = statePath;
        _client = client ?? new HelperClient();
        _query = query ?? HelperSetup.Query;
        _launch = launch ?? ElevatedLauncher.RunAsync;
        _timer.Tick += (_, _) => _ = PollAsync();
        SavedState saved = ReadState(statePath);
        _lastEventId = saved.Cursor;
        _runId = saved.Run;
        _resumedFromFile = saved.Cursor > 0;
        _held = saved.Held;
        _ownRequests.AddRange(saved.Own);
        StateUnreadable = saved.Unreadable;

        // A setup run this app started but didn't see the end of (BlindsDown was closed meanwhile): its IDs count until
        // the first answer has been read, so its unlock is still recognised, and are forgotten then.
        _setupDigestsToForget.UnionWith(saved.Own.Where(r => r.Setup).Select(r => r.Digest));
    }

    /// <summary><see cref="State"/> or <see cref="DeviceStatus"/> changed.</summary>
    public event Action? Changed;

    /// <summary>An alert from the helper (e.g. a camera was turned back on while locked).</summary>
    public event Action<HelperEvent>? EventReceived;

    /// <summary>The changes to the helper's lock in one answer, oldest first, each marked with whether this app (or its
    /// --restore, or a setup run it started) made it. Raised after <see cref="Changed"/>, so <see cref="DeviceStatus"/>
    /// already shows the result, and before the answer's alerts (<see cref="EventReceived"/>).</summary>
    public event Action<HelperChangeBatch>? ChangesReceived;

    public HelperUiState State { get; private set; } = HelperUiState.NotInstalled;

    /// <summary>The helper's last known lock state and devices. Kept when it stops answering: a helper that
    /// crashed leaves its devices off, and when it starts again it re-applies its lock.</summary>
    public DeviceLockStatus DeviceStatus { get; private set; } = DeviceLockStatus.Unlocked;

    /// <summary>Installed (also while an install, update or removal is running).</summary>
    public bool IsInstalled => State.Availability != HelperAvailability.NotInstalled;

    /// <summary>An install, update or removal is running right now.</summary>
    public bool IsChanging => State.Availability == HelperAvailability.Busy;

    /// <summary>The helper answered its last request and speaks this app's protocol.</summary>
    public bool CanLock { get; private set; }

    public bool HasLock => DeviceStatus.State != LockState.Unlocked;

    /// <summary>The helper has told this app its lock state at least once (so <see cref="HasLock"/> is not just the
    /// "unlocked" default of a helper that never answered).</summary>
    public bool LockStateKnown => _statusKnown;

    /// <summary>A Lock or Unlock this app sent timed out and may still be carried out by the helper.</summary>
    public bool OwnChangePending => DateTimeOffset.UtcNow < _ownChangeUntil;

    /// <summary>
    /// The helper holds this user's lock, as far as this app knows: its last lock went through and nothing unlocked it
    /// since. Kept across runs, so a lock that is gone at the next start without a change in the helper's log (the log
    /// was lost) is noticed, while a lock that was never in force (the helper didn't answer then) raises no alarm.
    /// </summary>
    public bool HeldOwnLock => _held;

    /// <summary>Windows has said at least once whether the helper is installed (<see cref="IsInstalled"/> is not just the
    /// "not installed" default).</summary>
    public bool InstallStateKnown { get; private set; }

    /// <summary>helper-state.txt exists but couldn't be read at startup: changes made while BlindsDown wasn't running
    /// can't be checked this time.</summary>
    public bool StateUnreadable { get; }

    public void Start() => _timer.Start();

    public void SetFastPolling(bool fast) => _timer.Interval = fast ? FastPollMilliseconds : SlowPollMilliseconds;

    /// <summary>Records the ID of an Unlock that a separate BlindsDown process (--restore, or a setup run this app
    /// starts) is about to send, so the tray app doesn't take that change for another program's. Only its fingerprint is
    /// kept. Best effort.</summary>
    public static void NoteOwnRequest(string statePath, string requestId)
    {
        SavedState saved = ReadState(statePath);
        saved.Own.Add(new OwnRequest(HelperProtocol.DigestOf(requestId), HelperCommand.Unlock));
        WriteState(statePath, saved);
    }

    /// <summary>A new ID for a Lock or Unlock this app sends.</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Re-reads whether the helper is installed and asks it for its status.</summary>
    public async Task RefreshAsync()
    {
        HelperInstallInfo info;
        try
        {
            info = await Task.Run(_query);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the helper service's state failed", ex);
            info = new HelperInstallInfo(Installed: false, Running: false, InstalledVersion: null, QueryFailed: true);
        }

        if (_disposed)
        {
            return;
        }

        if (info.QueryFailed)
        {
            // Windows couldn't be asked: that says nothing about the helper, so nothing is forgotten.
            if (IsInstalled)
            {
                MarkNotResponding(State.HelperVersion, "the service manager couldn't be asked");
            }

            return;
        }

        InstallStateKnown = true;
        if (!info.Installed)
        {
            CanLock = false;
            _statusKnown = false;
            ForgetSetupIds(); // No helper is left to report the run's changes.
            SetState(HelperUiState.NotInstalled, DeviceLockStatus.Unlocked);
            return;
        }

        try
        {
            HelperRequest status = NewRequest(HelperCommand.Status);
            HelperResponse response = await _client.SendAsync(status, QuickTimeout);
            Accept(response, info.InstalledVersion, status.SinceEvent);
        }
        catch (HelperUnavailableException ex) when (ex.Kind == HelperUnavailableKind.Busy || (ex.Kind == HelperUnavailableKind.TimedOut && OwnChangePending))
        {
            // Busy with other clients, or still working on this app's own request: it is running; keep what we know.
        }
        catch (HelperUnavailableException ex)
        {
            MarkNotResponding(info.InstalledVersion, info.Running ? ex.Message : "The helper service isn't running.", ex.Kind == HelperUnavailableKind.Impostor ? ex.Message : null);
        }
    }

    /// <summary>Asks the helper to lock. Returns its status, or an error message.</summary>
    public async Task<(DeviceLockStatus? Status, string? Error)> LockAsync(DeviceLockRequest request, string source)
    {
        _changing = true;
        try
        {
            HelperRequest lockRequest = NewChange(HelperCommand.Lock, source) with { Lock = request };
            HelperResponse response = await _client.SendAsync(lockRequest, ChangeTimeout);
            NoteLateChange(response);
            Accept(response, State.HelperVersion, lockRequest.SinceEvent);
            if (response.Ok && response.Status is { State: not LockState.Unlocked })
            {
                SetHeld(true); // Also when the request changed nothing: the lock in force is now what this app asked for.
            }

            return response.Ok ? (response.Status, null) : (null, response.Error ?? "The helper refused.");
        }
        catch (HelperUnavailableException ex)
        {
            return (null, Unanswered(ex));
        }
        finally
        {
            _changing = false;
        }
    }

    /// <summary>Asks the helper to unlock. Returns its restore report (null when it had no lock), or an error.</summary>
    public async Task<(RestoreReport? Report, string? Error)> UnlockAsync(string source)
    {
        _changing = true;
        try
        {
            HelperRequest unlockRequest = NewChange(HelperCommand.Unlock, source);
            HelperResponse response = await _client.SendAsync(unlockRequest, ChangeTimeout);
            NoteLateChange(response);
            Accept(response, State.HelperVersion, unlockRequest.SinceEvent);
            if (response.Ok)
            {
                SetHeld(false);
            }

            return response.Ok ? (response.Report, null) : (null, response.Error ?? "The helper refused.");
        }
        catch (HelperUnavailableException ex)
        {
            return (null, Unanswered(ex));
        }
        finally
        {
            _changing = false;
        }
    }

    /// <summary>Installs, updates or removes the helper (one UAC prompt). Returns a sentence for the user when it
    /// didn't work, or null.</summary>
    public async Task<string?> RunSetupAsync(ElevatedTask task, IWin32Window? owner)
    {
        _changing = true;
        string busyText = task switch
        {
            ElevatedTask.InstallHelper => "Windows asks for your permission; after that, installing the camera helper takes a few seconds…",
            ElevatedTask.UpdateHelper => "Windows asks for your permission; after that, updating the camera helper takes a few seconds…",
            _ => "Windows asks for your permission; after that, removing the camera helper can take a minute while your devices are turned back on…",
        };
        bool wasInstalled = IsInstalled; // Before the "busy" state, which counts as installed.
        SetState(State with { Availability = HelperAvailability.Busy, Detail = busyText }, DeviceStatus);

        // A removal first turns everything back on, and a new install undoes a lock left by an earlier helper: those
        // unlocks carry IDs of this app's own, so they are never announced as another program's. An update (or an
        // install over an installed helper, which updates it) never unlocks, so it gets no IDs at all.
        string? requestId = task == ElevatedTask.UninstallHelper || (task == ElevatedTask.InstallHelper && !wasInstalled) ? NewRequestId() : null;
        IReadOnlyList<string> setupIds = requestId is null ? [] : HelperSetup.SetupRequestIds(requestId);
        foreach (string id in setupIds)
        {
            AddOwnRequest(id, HelperCommand.Unlock, setup: true);
        }

        SaveState();
        int? code;
        try
        {
            code = await _launch(task, owner, requestId);
        }
        catch (Exception ex)
        {
            Log.Error($"Helper setup ({task}) could not be started", ex);
            code = -1;
        }
        finally
        {
            _changing = false;
        }

        Log.Info($"Helper setup ({task}) finished with " + (code?.ToString(CultureInfo.InvariantCulture) ?? "UAC cancelled"));
        string? problem = code switch
        {
            null => "Windows didn't get permission, so nothing changed.",
            0 => null,
            2 => "This needs administrator rights.",
            3 => "Removed. A device that stayed off comes back when you restart the PC.",
            1 when task != ElevatedTask.UninstallHelper =>
                "That didn't work (error 1). The details are in setup.log in %ProgramData%\\BlindsDown, which only administrators can open (for example with Notepad run as administrator), and in BlindsDown's own log.",
            _ => $"That didn't work (error {code}). The details are in BlindsDown's log, %LocalAppData%\\BlindsDown\\blindsdown.log.",
        };

        // The run's IDs were on its command line: forgotten once the next answer is read (the run's own changes are in
        // by then), or as soon as the helper is gone.
        _setupDigestsToForget.UnionWith(setupIds.Select(HelperProtocol.DigestOf));

        // The log position is kept: its IDs keep increasing across a restart of the helper.
        _statusKnown = false;
        await RefreshAsync();
        if (IsChanging)
        {
            // No answer yet (Windows or the pipe was busy): not "being set up" any more, just not answering for now.
            MarkNotResponding(State.HelperVersion, "no answer right after the setup run");
        }
        if (task == ElevatedTask.UninstallHelper && (code is 0 or 3 || (InstallStateKnown && !IsInstalled)))
        {
            // Removed by the user (its service entry may outlive the run a little): its lock is over, as the removal turned
            // the devices back on first.
            SetHeld(false);
        }

        if (problem is not null)
        {
            SetState(State with { Detail = problem }, DeviceStatus);
        }

        return problem;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        Changed = null;
        EventReceived = null;
        ChangesReceived = null;
    }

    /// <summary>Forgets that the helper held this user's lock (it was removed, and that was announced).</summary>
    public void ForgetHeldLock() => SetHeld(false);

    /// <summary>0 when the helper is this app's version, below 0 when it is older (or unknown), above 0 when newer.</summary>
    internal static int CompareToApp(string? helperVersion) => CompareVersions(helperVersion, AppVersion.Text);

    internal static int CompareVersions(string? helperVersion, string appVersion)
    {
        if (string.Equals(helperVersion, appVersion, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return Version.TryParse(Core(helperVersion), out Version? helper) && Version.TryParse(Core(appVersion), out Version? app)
            ? Math.Sign(helper.CompareTo(app)) switch { 0 => -1, int sign => sign } // Same numbers, different text: treat as older.
            : -1;

        static string? Core(string? text) => text?.Split('-', '+')[0];
    }

    internal static string Signature(DeviceLockStatus status) =>
        status.State + "|" + string.Join(";", status.Problems) + "|"
        + string.Join(";", status.Devices.Select(d => $"{d.Id}:{d.Name}:{d.Protection}:{d.Problem}:{d.Camera}"));

    /// <summary>After a timeout the helper may still finish the request: look again shortly rather than assume.</summary>
    private async Task RefreshSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_polling || _changing || _disposed)
        {
            return;
        }

        _polling = true;
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Polling the helper failed", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private HelperRequest NewRequest(HelperCommand command) => new()
    {
        Command = command,
        SinceEvent = Math.Max(0, _lastEventId),
        SinceRun = _lastEventId > 0 && _runId > 0 ? _runId : null,
        ClientVersion = AppVersion.Text,
    };

    /// <summary>A Lock or Unlock with a new ID of this app's own, remembered (its fingerprint, also on disk) before it is
    /// sent.</summary>
    private HelperRequest NewChange(HelperCommand command, string source)
    {
        string id = NewRequestId();
        AddOwnRequest(id, command);
        SaveState();
        return NewRequest(command) with { Source = source, RequestId = id };
    }

    private void AddOwnRequest(string id, HelperCommand command, bool setup = false)
    {
        _ownRequests.Add(new OwnRequest(HelperProtocol.DigestOf(id), command, setup));
        if (_ownRequests.Count > OwnRequestsKept)
        {
            _ownRequests.RemoveRange(0, _ownRequests.Count - OwnRequestsKept);
        }
    }

    /// <summary>
    /// Whether this app made <paramref name="change"/>: its fingerprint is one of this app's requests, sent with the
    /// command that makes that kind of change (an Unlock ends a lock; a Lock starts one or makes it cover less). An
    /// Unlock's ID makes one change, so it is forgotten once seen. A Lock's ID may come back once more: a narrowing the
    /// helper finishes later (a device it couldn't list during the request) belongs to the last Lock it got.
    /// </summary>
    private bool IsOwn(HelperChange change)
    {
        if (change.RequestDigest is not { Length: > 0 } digest)
        {
            return false;
        }

        HelperCommand command = change.Kind == HelperChangeKind.Unlocked ? HelperCommand.Unlock : HelperCommand.Lock;
        int index = _ownRequests.FindIndex(r => r.Command == command && string.Equals(r.Digest, digest, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        if (command == HelperCommand.Unlock)
        {
            _ownRequests.RemoveAt(index);
        }

        return true;
    }

    private void ForgetSetupIds()
    {
        if (_setupDigestsToForget.Count == 0)
        {
            return;
        }

        int removed = _ownRequests.RemoveAll(r => r.Setup && _setupDigestsToForget.Contains(r.Digest));
        _setupDigestsToForget.Clear();
        if (removed > 0)
        {
            SaveState();
        }
    }

    private void SetHeld(bool held)
    {
        if (_held != held)
        {
            _held = held;
            SaveState();
        }
    }

    /// <param name="sentPosition">The <see cref="HelperRequest.SinceEvent"/> the request carried.</param>
    private void Accept(HelperResponse response, string? installedVersion, long sentPosition)
    {
        string? version = response.HelperVersion is { Length: > 0 } v ? v : installedVersion;
        int age = CompareToApp(version);
        if (!response.Ok && response.Error is HelperTimeouts.BusyAnswer or HelperTimeouts.StillWorkingAnswer)
        {
            // Busy (nothing done) or still finishing this app's request (NoteLateChange covers that): neither says the
            // helper is broken, and neither carries its lock state or alerts.
            return;
        }

        if (!response.Ok && response.Error?.Contains("version", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Another BlindsDown version's helper: it can't understand this app, so it can't lock for it. Only an older
            // one is offered an update; replacing a newer one asks first (Settings).
            CanLock = false;
            SetState(
                age > 0
                    ? new HelperUiState(HelperAvailability.NeedsUpdate, version, $"The camera helper is from a newer BlindsDown ({version}), so it can't lock for this one. Use that BlindsDown, or replace the helper.")
                    : new HelperUiState(HelperAvailability.NeedsUpdate, version, "The camera helper is from an older BlindsDown, so it can't lock for this one. Update it."),
                DeviceStatus);
            return;
        }

        if (!response.Ok && response.Status is null)
        {
            // The helper answered but can't act (its device lock didn't start, it is stopping...). Not usable, and the
            // answer carries no alert position, so the cursor stays where it is.
            CanLock = false;
            SetState(
                new HelperUiState(HelperAvailability.NotResponding, version, "The camera helper isn't working: " + (response.Error ?? "unknown error").TrimEnd('.') + ". Repair it in Settings > Camera."),
                DeviceStatus);
            return;
        }

        CanLock = true;
        if (response.Status is not null)
        {
            _statusKnown = true;
        }

        // What happened since the last answer, oldest first. On the very first answer of a run that has no saved position
        // nothing is replayed; with a saved position, lock changes made meanwhile are, but old alerts aren't. When the
        // position was ahead of the helper (an earlier run's clock was ahead), it answers with everything from its
        // current run, all of it new here.
        // A reset answer to a request whose position has moved on meanwhile (another answer was read first, maybe a reset
        // one too) is read like any other, so nothing is taken twice.
        bool reset = response.SinceReset && sentPosition == Math.Max(0, _lastEventId);
        var changes = new List<HelperChange>();
        var events = new List<HelperEvent>();
        if (_lastEventId >= 0)
        {
            long after = reset ? long.MinValue : _lastEventId;
            changes.AddRange(response.Changes.Where(c => c.Id > after).OrderBy(c => c.Id));
            if (!_resumedFromFile)
            {
                events.AddRange(response.Events.Where(e => e.Id > after).OrderBy(e => e.Id));
            }
        }

        _resumedFromFile = false;

        // Its IDs are times in milliseconds, so 0 means "none yet". The position only moves back when the helper says
        // it started over; an older answer that arrives late must not make the same changes come again.
        long position = reset ? response.LastEventId : Math.Max(_lastEventId, response.LastEventId);
        bool positionFromThisAnswer = reset || response.LastEventId >= _lastEventId;
        long run = positionFromThisAnswer && response.RunId > 0 ? response.RunId : _runId;
        if (response.LastEventId > 0 && (position != _lastEventId || run != _runId))
        {
            _lastEventId = position;
            _runId = run;
            SaveState();
        }

        bool heldBefore = _held;
        var seen = new List<SeenHelperChange>(changes.Count);
        foreach (HelperChange change in changes)
        {
            bool own = IsOwn(change);
            seen.Add(new SeenHelperChange(change, own));
            if (change.Kind == HelperChangeKind.Unlocked)
            {
                _held = false;
            }
            else if (change.Kind == HelperChangeKind.Locked && own)
            {
                _held = true;
            }
        }

        if (seen.Count > 0)
        {
            SaveState();
        }

        ForgetSetupIds();

        HelperUiState state = age switch
        {
            0 => new HelperUiState(HelperAvailability.Working, version),
            > 0 => new HelperUiState(HelperAvailability.Working, version, $"The camera helper is from a newer BlindsDown ({version}). It works, but using that BlindsDown keeps the two in step."),
            _ => new HelperUiState(HelperAvailability.NeedsUpdate, version),
        };
        SetState(state, response.Status ?? DeviceStatus);

        if (seen.Count > 0)
        {
            ChangesReceived?.Invoke(new HelperChangeBatch(seen, heldBefore));
        }

        // The engine's alert about a lock that left devices out says the same as the change it belongs to (which says who
        // made it): only one without such a change in this answer (it got lost, or came from elsewhere) is passed on.
        HashSet<string> narrowedNames = changes
            .Where(c => c.Kind == HelperChangeKind.Narrowed)
            .SelectMany(c => c.TurnedOn.Concat(c.Released))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (HelperEvent helperEvent in events)
        {
            if (IsNarrowedAlert(helperEvent)
                && helperEvent.DeviceName.Split(", ", StringSplitOptions.RemoveEmptyEntries).All(narrowedNames.Contains))
            {
                Log.Info("Helper: " + helperEvent.Message);
                continue;
            }

            EventReceived?.Invoke(helperEvent);
        }
    }

    /// <summary>The helper engine's alert that a change to the lock left devices out (DeviceLockEngine.NarrowedAlertPrefix).</summary>
    internal static bool IsNarrowedAlert(HelperEvent helperEvent) =>
        helperEvent.Message.StartsWith("A change to the lock left", StringComparison.Ordinal);

    /// <summary>The helper said it is still working on the request (it finishes on its own; the status shows it).</summary>
    private void NoteLateChange(HelperResponse response)
    {
        if (!response.Ok && response.Error == HelperTimeouts.StillWorkingAnswer)
        {
            _ownChangeUntil = DateTimeOffset.UtcNow + LateChangeWindow;
        }
    }

    /// <summary>A Lock or Unlock that got no answer. Only one that reached the helper (timed out) may still be carried
    /// out later; when the pipe was busy or taken by another program, nothing was sent, so nothing will happen.</summary>
    private string Unanswered(HelperUnavailableException ex)
    {
        switch (ex.Kind)
        {
            case HelperUnavailableKind.TimedOut:
                _ownChangeUntil = DateTimeOffset.UtcNow + LateChangeWindow;
                break;
            case HelperUnavailableKind.Busy:
                // The helper answers, it was just busy: nothing changed, and it isn't broken.
                break;
            default:
                MarkNotResponding(State.HelperVersion, ex.Message, ex.Kind == HelperUnavailableKind.Impostor ? ex.Message : null);
                break;
        }

        _ = RefreshSoonAsync();
        return ex.Message;
    }

    /// <param name="version">The helper's version, if known.</param>
    /// <param name="reason">For the log.</param>
    /// <param name="detail">For the user; null for the usual "isn't answering" text. A pipe held by another program
    /// can't be repaired, so that case says what helps instead.</param>
    private void MarkNotResponding(string? version, string reason, string? detail = null)
    {
        CanLock = false;
        if (State.Availability != HelperAvailability.NotResponding)
        {
            Log.Warn("The camera helper isn't answering: " + reason);
        }

        SetState(new HelperUiState(HelperAvailability.NotResponding, version, detail ?? "The camera helper isn't answering. Repair it in Settings > Camera."), DeviceStatus);
    }

    private void SetState(HelperUiState state, DeviceLockStatus status)
    {
        // Every poll brings a new status object; only a real change is passed on (the tray and overlay redraw).
        if (_disposed || (state == State && Signature(status) == Signature(DeviceStatus)))
        {
            return;
        }

        State = state;
        DeviceStatus = status;
        Changed?.Invoke();
    }

    private void SaveState()
    {
        if (_statePath is not null)
        {
            WriteState(_statePath, new SavedState(_lastEventId, _held, _ownRequests) { Run = _runId });
        }
    }

    /// <summary>
    /// "cursor=123", "run=456" (the helper run that position came from), "held=1" when the helper holds this user's lock, then one "own=L:..." (Lock), "own=U:..." (Unlock) or
    /// "own=S:..." (an unlock of a setup run this app started) line per request fingerprint, oldest first. Missing: no
    /// position, no lock, no requests. Written atomically, so a copy left by an interrupted save is used when the file
    /// itself is gone; when one exists but can't be read, <see cref="SavedState.Unreadable"/> says so. Lines this version
    /// doesn't know (an earlier format kept the IDs themselves) are skipped.
    /// </summary>
    internal static SavedState ReadState(string? path)
    {
        var saved = new SavedState(-1, false, []);
        if (path is null)
        {
            return saved;
        }

        bool unreadable = false;

        // An older copy is used when the file itself is gone or unreadable; the latter is still reported (the copy may be
        // a save behind).
        foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
        {
            string[] lines;
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                lines = File.ReadAllLines(candidate, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Could not read the camera helper position: " + ex.Message);
                unreadable = true;
                continue;
            }

            foreach (string line in lines)
            {
                if (line.StartsWith("cursor=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                {
                    saved = saved with { Cursor = value };
                }
                else if (line.StartsWith("run=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out long run))
                {
                    saved = saved with { Run = run };
                }
                else if (line == "held=1")
                {
                    saved = saved with { Held = true };
                }
                else if (OwnRequest.Parse(line) is OwnRequest own)
                {
                    saved.Own.Add(own);
                }
            }

            if (saved.Own.Count > OwnRequestsKept)
            {
                saved.Own.RemoveRange(0, saved.Own.Count - OwnRequestsKept);
            }

            return saved with { Unreadable = unreadable };
        }

        return saved with { Unreadable = unreadable };
    }

    internal static void WriteState(string path, SavedState state)
    {
        try
        {
            var text = new StringBuilder();
            text.Append("cursor=").Append(state.Cursor.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (state.Run > 0)
            {
                text.Append("run=").Append(state.Run.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            if (state.Held)
            {
                text.Append("held=1\n");
            }

            foreach (OwnRequest own in state.Own.TakeLast(OwnRequestsKept))
            {
                text.Append(own.Line).Append('\n');
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Core.IO.AtomicFile.WriteAllText(path, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the camera helper position: " + ex.Message);
        }
    }

    /// <summary>What helper-state.txt keeps.</summary>
    /// <param name="Unreadable">A copy of the file exists but none could be read.</param>
    internal sealed record SavedState(long Cursor, bool Held, List<OwnRequest> Own, bool Unreadable = false)
    {
        /// <summary>The helper run the cursor came from (0: not known).</summary>
        public long Run { get; init; }
    }

    /// <summary>One of this app's requests: the fingerprint of its ID (<see cref="HelperProtocol.DigestOf"/>; the ID itself
    /// is never stored) and the command it was sent with.</summary>
    /// <param name="Setup">An unlock of a setup run this app started (forgotten after the run).</param>
    internal sealed record OwnRequest(string Digest, HelperCommand Command, bool Setup = false)
    {
        private const int DigestLength = 64;

        public string Line => "own=" + (Setup ? "S:" : Command == HelperCommand.Lock ? "L:" : "U:") + Digest;

        public static OwnRequest? Parse(string line)
        {
            if (line.Length != 6 + DigestLength || !line.StartsWith("own=", StringComparison.Ordinal) || line[5] != ':'
                || line[6..].Any(c => !char.IsAsciiHexDigitLower(c)))
            {
                return null;
            }

            return line[4] switch
            {
                'L' => new OwnRequest(line[6..], HelperCommand.Lock),
                'U' => new OwnRequest(line[6..], HelperCommand.Unlock),
                'S' => new OwnRequest(line[6..], HelperCommand.Unlock, Setup: true),
                _ => null,
            };
        }
    }
}

/// <summary>A change to the helper's lock as this app sees it.</summary>
/// <param name="Own">This app made it (or its --restore, or a setup run it started).</param>
internal sealed record SeenHelperChange(HelperChange Change, bool Own);

/// <summary>The changes to the helper's lock in one of its answers, oldest first.</summary>
/// <param name="HeldBefore">Before them, the helper held this user's lock (<see cref="HelperLink.HeldOwnLock"/>).</param>
internal sealed record HelperChangeBatch(IReadOnlyList<SeenHelperChange> Changes, bool HeldBefore);
