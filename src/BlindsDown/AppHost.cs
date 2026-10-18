using System.Globalization;
using BlindsDown.Audio;
using BlindsDown.Core.Helper;
using BlindsDown.Core.IO;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;
using BlindsDown.Core.Usage;
using BlindsDown.Devices.Pnp;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using BlindsDown.Usage;
using Microsoft.Win32;

namespace BlindsDown;

/// <summary>
/// Wires everything together. UI objects live on the UI thread; device and usage logic lives on the
/// <see cref="EngineThread"/>. Events from the engine are marshalled back to the UI with <see cref="OnUi"/>.
/// The lock has two parts: the microphones (this app, no admin rights) and, when the optional helper is installed,
/// cameras and microphone hardware (<see cref="HelperLink"/>). Locking does the microphones first; unlocking does
/// the helper first, so microphone hardware is back before its microphones are restored.
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    private static readonly TimeSpan UsageAlertRepeat = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OperationPatience = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HistoryPruneInterval = TimeSpan.FromHours(6);

    /// <summary>How long a lock that goes on at startup waits for the helper before saying the cameras aren't locked.</summary>
    private static readonly TimeSpan HelperStartPatience = TimeSpan.FromSeconds(30);


    /// <summary>Browsers capture for many websites; Windows only tells us the browser.</summary>
    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe",
    };

    private readonly AppPaths _paths;
    private readonly SingleInstance _instance;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _history;
    private readonly LockEventLog _lockEvents;
    private readonly EngineThread _engineThread;
    private readonly ProcessIdentityResolver _identities = new();
    private readonly Control _uiMarshal = new();
    private readonly TrayController _tray = new();
    private readonly HotkeyManager _hotkey = new();
    private readonly LockOverlayManager _overlay = new();
    private readonly OsdFlash _osd = new();
    private readonly SoundService _sounds = new();
    private readonly AlertPopupManager _popups = new();
    private readonly HelperLink _helper;
    private readonly Dictionary<string, DateTimeOffset> _lastUsageAlert = new(StringComparer.OrdinalIgnoreCase);

    // Owned by the engine thread.
    private CoreAudioMicBackend? _backend;
    private LockEngine? _engine;
    private AudioDeviceWatcher? _watcher;
    private MicSessionMonitor? _sessions;
    private CameraActivityMonitor? _cameras;
    private ConsentStoreMonitor? _consent;
    private UsageTracker? _tracker;

    // UI thread state. Settings are replaced as a whole on save, never mutated in place, so the engine
    // thread can safely read the current instance.
    private volatile AppSettings _settings;
    private LockStatusInfo _micStatus = LockStatusInfo.Unlocked;
    private LockStatusInfo _status = LockStatusInfo.Unlocked;
    private IReadOnlyList<PnpDevice> _localCameras = [];
    private IReadOnlyList<UsageSession> _activeUsage = [];
    private IReadOnlyList<UsageRecord> _historyCache = [];
    private IReadOnlyList<LockEventRecord> _lockEventCache = [];
    private ActivityForm? _activity;
    private SettingsForm? _openSettings;
    private bool _engineReady;
    private bool _busy;
    private bool _settingsOpen;
    private bool _helperChanging;
    private bool _updateOffered;

    /// <summary>Set while a lock or unlock runs, so half-done states (microphones locked, cameras not yet) show as
    /// "locking" instead of a "partially locked" flash.</summary>
    private LockState? _transition;
    private bool _helperHadLock;
    private HelperAvailability _lastHelperAvailability = HelperAvailability.NotInstalled;


    /// <summary>The request last sent to the helper in this lock, and the hardware it was asked to leave on.</summary>
    private DeviceLockRequest? _lastHelperRequest;
    private readonly HashSet<string> _lastKeepEnabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A change the app itself sent to the helper outside a lock or unlock (keeping new excluded mic hardware on).</summary>
    private bool _keepOnChange;

    /// <summary>The crash-recovery question is open: no other lock or unlock may run until it is answered.</summary>
    private bool _recovering;

    /// <summary>Startup (helper check, recovery, first-run setup) is finished; commands from other programs wait until then.</summary>
    private bool _startupDone;
    private readonly Queue<InstanceCommand> _pendingCommands = new();

    /// <summary>An automatic lock (Win+L, sleep) that arrived while a lock or unlock was running.</summary>
    private string? _pendingAutoLock;
    private bool _exitPrompting;
    private bool _runningCommands;

    /// <summary>Another program made the helper's lock cover less: shown as partially locked until the lock is redone.</summary>
    private bool _helperNarrowedElsewhere;

    /// <summary>The changes behind the alarm about another program shown in this run (the start-up check doesn't repeat
    /// it, and its text is rebuilt when BlindsDown has put the lock back since).</summary>
    private (IReadOnlyList<HelperChange> Changes, bool BeforeStart)? _foreignAlarm;

    /// <summary>Counts <see cref="ShowForeignAlarm"/> calls, so a lock request can tell whether its own answer raised one.</summary>
    private int _foreignAlarmsShown;

    /// <summary>The last alarm about another program as shown, so "Lock again" can show it again when it didn't relock.</summary>
    private AlertModel? _lastForeignAlarm;

    /// <summary>The amber notice that changes from before this start couldn't be checked (helper-state.txt unreadable).</summary>
    private bool _uncheckedNoticeShown;

    /// <summary>The timeline entry of an "Unlock and exit", written once it is clear whether BlindsDown really exits.</summary>
    private (string? Summary, bool Problems)? _exitUnlockRecord;

    /// <summary>When the microphones were locked (UTC): a change to the helper's lock from before then wasn't to this lock.
    /// MinValue for a lock that goes on from before this start.</summary>
    private DateTimeOffset _micsLockedSince = DateTimeOffset.MinValue;

    /// <summary>The microphones were still locked at startup but the helper didn't answer yet: its part of the lock is
    /// checked, and put back, as soon as it does.</summary>
    private bool _resumeHelperLockPending;

    /// <summary>When this run started: a lock change from before it happened while BlindsDown wasn't running.</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>A command from another program that <see cref="RunCommandAsync"/> holds while it waits for its turn (before
    /// its lock or unlock starts); with <see cref="_pendingCommands"/>, what an exit must not drop silently.</summary>
    private InstanceCommand? _waitingCommand;

    /// <summary>Commands received from other programs so far (to see whether one arrived while a question was open).</summary>
    private int _commandsReceived;

    /// <summary>The restore report of an "Unlock and exit" whose exit was then cancelled (shown instead).</summary>
    private RestoreReport? _exitUnlockReport;

    private const string StillBusy = "BlindsDown is still locking or unlocking.";
    private bool _exiting;
    private bool _disposed;

    public AppHost(AppPaths paths, SingleInstance instance, InstanceCommand startupCommand, bool autostarted)
    {
        _paths = paths;
        _instance = instance;
        _ = _uiMarshal.Handle; // Create the window handle so BeginInvoke works from other threads.
        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        _history = new HistoryStore(paths.HistoryFile);
        _lockEvents = new LockEventLog(paths.LockEventsFile);
        _engineThread = new EngineThread("BlindsDown devices", ex => Log.Error("Device thread error", ex));
        _helper = new HelperLink(paths.HelperStateFile);
        _helperHadLock = _helper.HeldOwnLock; // A helper removed while BlindsDown wasn't running is noticed at the start.

        NetworkTripwire.Install(name => OnUi(() =>
        {
            Log.Error("Networking assembly loaded: " + name);
            _popups.Show(new AlertModel(
                "Unexpected networking code loaded",
                $"The component \"{name}\" was loaded into BlindsDown. BlindsDown never uses the network, so this should not happen. Please report it.",
                AlertSeverity.Danger,
                Sticky: true));
        }));

        _tray.ToggleRequested += () => _ = ToggleAsync(LockEventSource.Tray);
        _tray.ActivityRequested += ShowActivity;
        _tray.SettingsRequested += () => ShowSettings();
        _tray.AboutRequested += () => Dialogs.ShowAbout(AppVersion.Text);
        _tray.ExitRequested += () => _ = ExitAsync();
        _hotkey.Pressed += OnHotkeyPressed;
        _helper.Changed += OnHelperChanged;
        _helper.EventReceived += OnHelperEvent;
        _helper.ChangesReceived += OnHelperChanges;

        // Commands from other programs (--lock, --unlock...) run in the order they came, after startup (see HandleCommand);
        // the one that started BlindsDown came first.
        if (startupCommand != InstanceCommand.Show)
        {
            _pendingCommands.Enqueue(startupCommand);
        }

        _instance.CommandReceived += command => OnUi(() => HandleCommand(command));
        _instance.Listen();
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _osd.HideFromScreenCapture = _settings.HideOverlayFromScreenCapture;
        _popups.HideFromScreenCapture = _settings.HideOverlayFromScreenCapture;
        _tray.Show();
        _tray.SetStatus(_status, _settings.GetHotkey());
        _ = StartAsync(autostarted);
    }

    private async Task StartAsync(bool autostarted)
    {
        StartupRecovery? recovery;
        try
        {
            Task<StartupRecovery?> start = _engineThread.InvokeAsync(StartEngine);
            if (await Task.WhenAny(start, Task.Delay(OperationPatience)) != start)
            {
                Log.Warn("Windows audio has not answered at startup yet.");
                _popups.Show(new AlertModel(
                    "Waiting for Windows audio",
                    "BlindsDown can't reach your microphones yet, so the lock isn't available. It keeps trying.",
                    AlertSeverity.Warning,
                    Sticky: true,
                    GroupKey: "audio-not-responding"));
            }

            recovery = await start;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the device engine", ex);
            _popups.Show(new AlertModel(
                "BlindsDown can't reach your microphones",
                "Windows audio did not respond, so the lock is not available right now. Try restarting BlindsDown. Details are in the log file.",
                AlertSeverity.Danger,
                Sticky: true));
            FinishStartup();
            return;
        }

        try
        {
            // Before any recovery decision: the helper may still hold a lock (it keeps its lock across restarts), and lock
            // changes another program made while BlindsDown wasn't running are reported now, which needs the microphones'
            // state (a lock that goes on) to be known first.
            if (_engine is LockEngine started)
            {
                _micStatus = await _engineThread.InvokeAsync(() => started.Status);
            }

            await _helper.RefreshAsync();
            CheckHelperRemoved(); // Also when nothing changed since the helper link started ("not installed").
            if (_helper.StateUnreadable && _helper.IsInstalled && !_uncheckedNoticeShown)
            {
                _popups.Show(new AlertModel(
                    "Couldn't check the camera lock's history",
                    "BlindsDown couldn't read its file %LocalAppData%\\BlindsDown\\helper-state.txt, so it can't tell whether another program changed the camera lock while BlindsDown wasn't running. Changes from now on are checked as usual.",
                    AlertSeverity.Warning,
                    Sticky: true));
            }

            _helper.Start();
            await RefreshLocalCamerasAsync();

            _engineReady = true;
            RegisterHotkey(showErrors: true);

            if (recovery is not null)
            {
                await HandleRecoveryAsync(recovery);
            }

            if (!_settings.FirstRunDone)
            {
                RunFirstTimeSetup();
            }
            else
            {
                OfferCameraLockOnce();
            }

            if (SystemInfo.IsRemoteSession() && !autostarted)
            {
                _popups.Show(new AlertModel(
                    "Remote Desktop session",
                    "In a Remote Desktop session the microphones you see belong to the remote connection. The lock applies to those.",
                    AlertSeverity.Info));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Startup did not finish", ex);
        }
        finally
        {
            FinishStartup();
        }
    }

    /// <summary>Startup is over: run the commands that arrived meanwhile, in order, and an automatic lock that waited.</summary>
    private void FinishStartup()
    {
        _startupDone = true;
        _ = RunPendingCommandsAsync();
        RunPendingAutoLock();
    }

    /// <summary>One consumer for all commands, so they run in the order they came.</summary>
    private async Task RunPendingCommandsAsync()
    {
        if (_runningCommands)
        {
            return;
        }

        _runningCommands = true;
        try
        {
            while (_pendingCommands.TryDequeue(out InstanceCommand command))
            {
                await RunCommandAsync(command);
            }
        }
        finally
        {
            _runningCommands = false;
        }
    }

    /// <summary>Runs on the engine thread.</summary>
    private StartupRecovery? StartEngine()
    {
        var backend = new CoreAudioMicBackend();
        var engine = new LockEngine(
            backend,
            new FileJournalStore(_paths.JournalFile),
            new FilePendingRestoreStore(_paths.PendingRestoreFile),
            SystemClock.Instance,
            () => _settings);
        engine.StatusChanged += status => OnUi(() =>
        {
            _micStatus = status;
            RecomputeStatus();
            _ = KeepHardwareOnAsync();
        });
        engine.AlertRaised += alert => OnUi(() => ShowEngineAlert(alert, fromHelper: false));

        var tracker = new UsageTracker(SystemClock.Instance);
        tracker.Started += _ => PublishActiveUsage();
        tracker.Confirmed += session => OnUi(() => OnUsageConfirmed(session));
        tracker.Ended += OnUsageEnded;

        var watcher = new AudioDeviceWatcher(_engineThread);
        watcher.EndpointChanged += endpointId =>
        {
            engine.OnEndpointChanged(endpointId);
            ReportMicSessions();
        };
        watcher.MuteChanged += engine.OnMuteChanged;

        var sessions = new MicSessionMonitor(_engineThread, _identities);
        sessions.Changed += ReportMicSessions;

        var cameras = new CameraActivityMonitor(_engineThread, _identities);
        cameras.Changed += ReportCameraSessions;

        var consent = new ConsentStoreMonitor(_identities);
        consent.Changed += () => _engineThread.Post(ReportUsageRegistry);

        _backend = backend;
        _engine = engine;
        _tracker = tracker;
        _watcher = watcher;
        _sessions = sessions;
        _cameras = cameras;
        _consent = consent;

        StartupRecovery? recovery = engine.Initialize();
        if (engine.JournalWasCorrupt)
        {
            OnUi(() => _popups.Show(new AlertModel(
                "Lock record damaged",
                "BlindsDown's record of the last lock could not be read, so it can't tell which microphones it muted or turned off. Please check in Windows Sound settings that your microphones are on and not muted.",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [new AlertAction("Open Sound settings", SafeShell.OpenSoundSettings)])));
        }

        TryStart(watcher.Start, "device watcher");
        TryStart(sessions.Start, "audio session monitor");
        TryStart(cameras.Start, "camera activity monitor");
        TryStart(consent.Start, "usage registry monitor");

        _engineThread.Every(TimeSpan.FromSeconds(5), engine.Reconcile);
        _engineThread.Every(TimeSpan.FromSeconds(2), ReportMicSessions);
        _engineThread.Every(TimeSpan.FromSeconds(2), ReportCameraSessions);
        _engineThread.Every(TimeSpan.FromSeconds(1), tracker.Tick);
        _engineThread.Every(TimeSpan.FromSeconds(10), ReportUsageRegistry);
        _engineThread.Every(HistoryPruneInterval, PruneHistory);
        ReportMicSessions();
        ReportCameraSessions();
        ReportUsageRegistry();
        PruneHistory();
        return recovery;
    }

    private static void TryStart(Action start, string what)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the " + what, ex);
        }
    }

    // ---------------------------------------------------------------- locking

    /// <summary>The global hotkey. While the Settings hotkey box is taking keystrokes, Windows still delivers the
    /// registered combination here instead of to the box, so it is handed to the box rather than toggling the lock.</summary>
    private void OnHotkeyPressed()
    {
        if (_openSettings is { IsHotkeyBoxCapturing: true } settings && _hotkey.Current is Hotkey current)
        {
            settings.OfferCapturedHotkey(current);
            return;
        }

        _ = ToggleAsync(LockEventSource.Hotkey);
    }

    private Task ToggleAsync(LockEventSource source)
    {
        if (!_engineReady || _busy || _recovering || _exitPrompting || _exiting)
        {
            return Task.CompletedTask;
        }

        // Microphones unlocked, but a helper that can't be reached still holds an earlier lock: the press locks the
        // microphones (the user wants protection now); the cameras can only be unlocked once the helper answers again.
        bool lockMicrophones = _micStatus.State == LockState.Unlocked && (!_helper.HasLock || !_helper.CanLock);
        return _status.State == LockState.Unlocked || lockMicrophones ? LockAsync(source) : UnlockAsync(source);
    }

    private async Task LockAsync(LockEventSource source)
    {
        if (_busy || (_recovering && source != LockEventSource.Recovery) || _engine is not LockEngine engine)
        {
            return;
        }

        _busy = true;
        _transition = LockState.Locking;
        _resumeHelperLockPending = false; // This lock sends the helper the whole request anyway.
        if (_micStatus.State == LockState.Unlocked)
        {
            _micsLockedSince = DateTimeOffset.UtcNow;
            _foreignAlarm = null; // A new lock: an earlier alarm's changes are not about it.
        }

        try
        {
            _micStatus = await WithPatienceAsync(_engineThread.InvokeAsync(engine.Lock), "locking");
            string? helperError = await LockHelperAsync(engine, LogName(source));
            _transition = null;
            RecomputeStatus();
            LockStatusInfo status = _status;
            Log.Info($"Lock ({source}): {status.State}");
            if (_settings.PlaySounds)
            {
                _sounds.Play(SoundCue.Lock);
            }

            _osd.Show(status.State, LockedText(status), cameras: CamerasLocked(status));
            if (helperError is not null)
            {
                ShowHelperLockFailed(helperError);
            }

            ReportAppsAlreadyUsing(status);
            RecordLockEvent(LockEventAction.Lock, source, LockedText(status), status.State == LockState.PartiallyLocked);
        }
        catch (Exception ex)
        {
            Log.Error("Lock failed", ex);
            _transition = null;
            RecomputeStatus();
            _popups.Show(new AlertModel("Lock failed", "BlindsDown could not lock your microphones: " + ex.Message, AlertSeverity.Danger, Sticky: true));
        }
        finally
        {
            _transition = null;
            _busy = false;
            RunPendingAutoLock();
        }
    }

    /// <summary>The helper's part of a lock: cameras, and microphone hardware when chosen. Returns an error
    /// message, or null when it worked or wasn't wanted.</summary>
    /// <param name="afterSetup">Called by <see cref="RunHelperSetupAsync"/> once its setup run has ended.</param>
    private async Task<string?> LockHelperAsync(LockEngine engine, string source, bool afterSetup = false)
    {
        AppSettings settings = _settings;
        if (!WantsHelper(settings) || !_helper.IsInstalled)
        {
            return null;
        }

        if (_helperChanging && !afterSetup)
        {
            // The helper is being installed, updated or removed (or its permission prompt is open): when that ends,
            // RunHelperSetupAsync locks the cameras if the microphones are still locked.
            return null;
        }

        if (_helper.IsChanging)
        {
            return afterSetup ? "the camera helper isn't answering yet after its setup" : null;
        }

        if (!_helper.CanLock)
        {
            await _helper.RefreshAsync();
            if (!_helper.CanLock)
            {
                return _helper.State.Detail ?? "it isn't answering";
            }
        }

        // Journal first: from now on the mic engine follows a default that Windows moves when the helper turns a
        // microphone's hardware (or a camera with a microphone) off, even if this app never hears the helper's answer
        // (its safety poll keeps following). Safe to call before and after: it never records a default someone picked.
        await _engineThread.InvokeAsync(engine.NoteDefaultsSettled);
        IReadOnlyList<string> keepOn = await _engineThread.InvokeAsync(engine.HardwareToKeepEnabled);
        DeviceLockRequest request = BuildHelperRequest(settings, keepOn);
        _lastHelperRequest = request;
        _lastKeepEnabled.Clear();
        _lastKeepEnabled.UnionWith(request.KeepEnabledInstanceIds);
        _helperNarrowedElsewhere = false;
        int alarmsBefore = _foreignAlarmsShown;
        (DeviceLockStatus? status, string? error) = await _helper.LockAsync(request, source);
        if (status is { State: LockState.Unlocked })
        {
            // The helper answered but could not start the lock (e.g. it can't save its record): nothing was changed.
            error ??= status.Problems.FirstOrDefault() ?? "it could not start the lock";
            status = null;
        }

        if (status is not null)
        {
            // This app's request is the lock in force now, also when the helper recorded no change for it (the same as, or
            // more than, what was there). Another program's change in that same answer is over: say so.
            _helperNarrowedElsewhere = false;
            if (_foreignAlarmsShown != alarmsBefore && _foreignAlarm is (IReadOnlyList<HelperChange> changes, bool beforeStart))
            {
                (string title, string message) = ForeignChangeText(changes, beforeStart, putBack: true);
                ShowForeignAlarm(title, message, offerRelock: false, ForeignSeverity(changes));
            }
        }

        if (status is not null)
        {
            // Turning hardware off makes Windows pick new default microphones a moment later; remember the
            // defaults once they settled, so unlocking can put the old ones back.
            await Task.Delay(1000);
            await _engineThread.InvokeAsync(engine.NoteDefaultsSettled);
        }

        return error;
    }

    /// <summary>
    /// An excluded or default microphone whose hardware got a new ID during the lock (moved to another USB port) must stay
    /// on: tell the helper to leave it on too. Only ever adds devices to keep on, so the helper only does less.
    /// </summary>
    private async Task KeepHardwareOnAsync()
    {
        if (_keepOnChange || _busy || _helperChanging || _lastHelperRequest is not { MicHardware: true } lastRequest
            || !_helper.HasLock || !_helper.CanLock || !_micStatus.IsLockedOrPartly || _engine is not LockEngine engine)
        {
            return;
        }

        _keepOnChange = true;
        bool retry = false;
        try
        {
            IReadOnlyList<string> now = await _engineThread.InvokeAsync(engine.HardwareToKeepEnabled);
            List<string> added = now.Where(id => !_lastKeepEnabled.Contains(id) && Fits(id)).ToList();
            if (added.Count == 0 || _busy || _lastHelperRequest is null)
            {
                return;
            }

            DeviceLockRequest request = lastRequest with { KeepEnabledInstanceIds = _lastKeepEnabled.Concat(added).Take(HelperProtocol.MaxExclusions).ToList() };
            Log.Info($"Asking the camera helper to keep {added.Count} more microphone device(s) on.");
            (DeviceLockStatus? status, string? error) = await _helper.LockAsync(request, "keep excluded or default microphone on");
            if (error is null && status is not null)
            {
                // Only what the helper took counts as sent; anything else is asked for again.
                _lastKeepEnabled.UnionWith(added);
                _lastHelperRequest = request;
            }
            else
            {
                Log.Warn("The camera helper didn't take the updated list of microphones to keep on: " + error);
                retry = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Updating the microphones the helper keeps on failed", ex);
            retry = true;
        }
        finally
        {
            _keepOnChange = false;
            if (retry)
            {
                _ = RetryKeepHardwareOnAsync();
            }
        }
    }

    private async Task RetryKeepHardwareOnAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await KeepHardwareOnAsync();
    }

    /// <summary>Unlocks and shows the restore report. Returns the report (null if there was no lock) and
    /// an error message if the unlock failed.</summary>
    private async Task<(RestoreReport? Report, string? Error)> UnlockAsync(LockEventSource source)
    {
        if (_busy || (_recovering && source != LockEventSource.Recovery))
        {
            // Never mistaken for "nothing to restore": callers such as Exit must not go on as if it had worked.
            return (null, StillBusy);
        }

        if (_engine is not LockEngine engine)
        {
            return (null, null);
        }

        _busy = true;
        _transition = LockState.Unlocking;
        try
        {
            if (_helper.HasLock)
            {
                // While the hardware is still off: record a default Windows moved when the helper turned it off, so the
                // microphone unlock can put it back once the hardware is on again.
                await _engineThread.InvokeAsync(engine.NoteDefaultsSettled);
            }

            (RestoreReport? helperReport, string? helperError, bool helperStateUnknown) = await UnlockHelperAsync(LogName(source));
            bool fromElsewhere = source == LockEventSource.OtherProgram;
            bool alarmShown = false;
            if (fromElsewhere && helperReport is not null)
            {
                // The helper has turned devices back on: say so now, not only once the microphones are done (Windows audio
                // may take its time, and BlindsDown may even be closed in between).
                ShowUnlockedByOtherProgram();
                alarmShown = true;
            }

            RestoreReport? micReport = await WithPatienceAsync(_engineThread.InvokeAsync(engine.Unlock), "unlocking");
            _micStatus = await _engineThread.InvokeAsync(() => engine.Status);
            _transition = null;
            RecomputeStatus();
            RestoreReport? report = RestoreReport.Combine(micReport, helperReport);
            Log.Info($"Unlock ({source}): " + (report?.Summary ?? "nothing to restore"));

            // An unlock the user didn't ask for directly (another program sent the command) must never be
            // quiet: always the sound, and a red popup that stays.
            if ((_settings.PlaySounds || fromElsewhere) && !alarmShown)
            {
                _sounds.Play(SoundCue.Unlock);
            }

            _osd.Show(_status.State, UnlockedText(report, helperError), cameras: report?.Lines.Any(l => l.Kind == DeviceKind.Camera) == true);
            if (fromElsewhere && report is not null && !alarmShown)
            {
                ShowUnlockedByOtherProgram();
            }

            if (helperError is not null)
            {
                string part = HelperHeldPart();
                _popups.Show(helperStateUnknown
                    ? new AlertModel(
                        $"Couldn't check your {part}",
                        $"The camera helper hasn't answered since BlindsDown started ({helperError.TrimEnd('.')}), so BlindsDown couldn't check whether it still has {part} turned off. If it has, BlindsDown tells you as soon as it answers; unlock again then.",
                        AlertSeverity.Warning,
                        Sticky: true,
                        Actions: [new AlertAction("Settings", () => ShowSettings("Camera"))],
                        GroupKey: "helper-unlock-failed")
                    : new AlertModel(
                        $"{char.ToUpperInvariant(part[0])}{part[1..]} may still be off",
                        // A restart does NOT help here: the helper keeps its lock record and locks the devices again at startup.
                        $"The camera helper didn't answer: {helperError.TrimEnd('.')}. It still has your {part} turned off, and it turns them off again after a restart. Unlock again once it answers, or choose Repair in Settings > Camera.",
                        AlertSeverity.Danger,
                        Sticky: true,
                        Actions: [new AlertAction("Settings", () => ShowSettings("Camera"))],
                        GroupKey: "helper-unlock-failed"));
            }

            if (report is not null && source != LockEventSource.Exit)
            {
                Dialogs.ShowRestoreReport(report, _popups);
            }

            if (source == LockEventSource.Exit)
            {
                // Written by ExitAsync once it is clear whether BlindsDown really exits.
                _exitUnlockRecord = (report?.Summary ?? helperError, helperError is not null || report is { AllExact: false });
            }
            else if (report is not null || helperError is not null)
            {
                RecordLockEvent(LockEventAction.Unlock, source, report?.Summary ?? helperError, helperError is not null || report is { AllExact: false });
            }

            return (report, helperError);
        }
        catch (Exception ex)
        {
            Log.Error("Unlock failed", ex);
            _transition = null;
            RecomputeStatus();
            RecordLockEvent(LockEventAction.Unlock, source, "Unlock failed: " + ex.Message, hadProblems: true);
            _popups.Show(new AlertModel(
                "Unlock failed",
                "BlindsDown could not restore your microphones: " + ex.Message + " Run \"BlindsDown.exe --restore\" or check Windows Sound settings.",
                AlertSeverity.Danger,
                Sticky: true,
                Actions: [new AlertAction("Open Sound settings", SafeShell.OpenSoundSettings)]));
            return (null, ex.Message);
        }
        finally
        {
            _transition = null;
            _busy = false;
            _lastHelperRequest = null;
            _lastKeepEnabled.Clear();
            _helperNarrowedElsewhere = false;
            _resumeHelperLockPending = false;
            _helperHadLock = HelperHoldsThisLock();
            if (_micStatus.State == LockState.Unlocked)
            {
                _foreignAlarm = null; // The user's own unlock: an earlier alarm's lock is over.
            }
            if (source != LockEventSource.Exit)
            {
                RunPendingAutoLock();
            }

            // On exit, ExitAsync decides: it drops a waiting automatic lock when BlindsDown really exits, else runs it.
        }
    }

    private void ShowUnlockedByOtherProgram()
    {
        _sounds.Play(SoundCue.Unlock);
        _popups.Show(new AlertModel(
            "Unlocked by another program",
            $"A program on this PC told BlindsDown to unlock (for example \"BlindsDown.exe --unlock\") at {DateTime.Now:t}. If that wasn't you, lock again and check which programs are running.",
            AlertSeverity.Danger,
            Sticky: true,
            GroupKey: "unlocked-by-other"));
    }

    /// <summary>The on-screen message after an unlock: what really happened, not the lock-direction defaults.</summary>
    private string? UnlockedText(RestoreReport? report, string? helperError)
    {
        if (helperError is not null)
        {
            string part = HelperHeldPart();
            return _micStatus.State == LockState.Unlocked ? $"Microphones unlocked; {part} may still be off" : $"{char.ToUpperInvariant(part[0])}{part[1..]} may still be off";
        }

        if (report is { AllExact: false })
        {
            return report.Summary;
        }

        return _status.State != LockState.Unlocked ? _status.Problems.FirstOrDefault() : null;
    }

    /// <summary>The helper's part of an unlock (first, so microphone hardware is back before its microphones are
    /// unmuted). Waits until Windows has brought those microphones back. <c>StateUnknown</c>: the helper never answered
    /// since BlindsDown started, so whether it still holds a lock isn't known.</summary>
    private async Task<(RestoreReport? Report, string? Error, bool StateUnknown)> UnlockHelperAsync(string source)
    {
        // A helper that isn't answering may still hold a lock from before, so it is asked anyway; but if it is known to
        // hold no lock, its silence is no reason to alarm the user.
        bool knownLock = _helper.HasLock;
        bool stateKnown = _helper.LockStateKnown;
        if (!_helper.IsInstalled || (!knownLock && _helper.CanLock && stateKnown))
        {
            return (null, null, false);
        }

        // Every device the helper has off: a camera can be a microphone's hardware too (MicrophoneReturn only waits for
        // ids that some microphone reports as its hardware).
        var hardwareIds = _helper.DeviceStatus.Devices
            .Where(d => d.Protection == DeviceProtection.DeviceDisabled)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        (RestoreReport? report, string? error) = await _helper.UnlockAsync(source);
        if (error is not null && !knownLock && stateKnown)
        {
            Log.Warn("The camera helper didn't answer the unlock (it had no lock): " + error);
            error = null;
        }

        if (error is null && hardwareIds.Count > 0)
        {
            try
            {
                await WaitForMicrophonesAsync(hardwareIds);
            }
            catch (Exception ex)
            {
                Log.Warn("Waiting for microphones to come back failed: " + ex.Message);
            }
        }

        return (report, error, error is not null && !stateKnown);
    }

    /// <summary>"cameras", "microphone hardware" or both: what the helper has off now, else what the settings lock.</summary>
    private string HelperHeldPart()
    {
        IReadOnlyList<DeviceLockInfo> devices = _helper.DeviceStatus.Devices;
        bool cameras = devices.Any(d => d.Kind == DeviceKind.Camera && d.Protection == DeviceProtection.DeviceDisabled);
        bool hardware = devices.Any(d => d.Kind == DeviceKind.MicrophoneHardware && d.Protection == DeviceProtection.DeviceDisabled);
        return cameras || hardware
            ? HelperPart(cameras, hardware)
            : HelperPart(_settings.LockCameras || !_settings.DisableMicOnlyHardware, _settings.DisableMicOnlyHardware);
    }

    /// <summary>After the helper turned microphone hardware back on, Windows needs a moment to list its microphones
    /// again; restoring them before that would wrongly report them as "not connected". Best effort: it never fails,
    /// so the microphones are always unlocked afterwards.</summary>
    private async Task WaitForMicrophonesAsync(HashSet<string> hardwareIds)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MicrophoneReturn.Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool missing;
            try
            {
                missing = await _engineThread.InvokeAsync(() => _backend is not null && MicrophoneReturn.AnyMissing(_backend, hardwareIds));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not check whether the microphones came back: " + ex.Message);
                missing = true; // Not back as far as we know: keep waiting until the deadline.
            }

            if (!missing)
            {
                return;
            }

            await Task.Delay(250);
        }

        Log.Warn("Some microphones had not come back yet after their hardware was turned on.");
    }

    private static bool WantsHelper(AppSettings settings) => settings.LockCameras || settings.DisableMicOnlyHardware;

    private static DeviceLockRequest BuildHelperRequest(AppSettings settings, IReadOnlyList<string> keepEnabled) => new()
    {
        Cameras = settings.LockCameras,
        MicHardware = settings.DisableMicOnlyHardware,
        IncludeInfrared = settings.IncludeInfraredCameras,
        IncludeVirtual = settings.IncludeVirtualCameras,
        Exclusions = settings.ExcludedDevices
            .Where(e => e.StableKey.StartsWith("cam|", StringComparison.Ordinal) || e.StableKey.StartsWith("hw|", StringComparison.Ordinal))
            .Where(e => Fits(e.StableKey) && Fits(e.DisplayName) && (e.LastKnownId is null || Fits(e.LastKnownId)))
            .Select(e => e with { TwinIds = null })
            .Take(HelperProtocol.MaxExclusions)
            .ToList(),
        KeepEnabledInstanceIds = keepEnabled.Where(Fits).Take(HelperProtocol.MaxExclusions).ToList(),
    };

    private static bool Fits(string value) => value.Length <= HelperProtocol.MaxStringLength;

    /// <summary>
    /// Waits for a lock/unlock on the device thread. If Windows audio doesn't answer for a while (a hung
    /// driver or audio service), the user is told instead of being left with a toggle that seems dead.
    /// The operation keeps running and its result is applied when Windows answers.
    /// </summary>
    private async Task<T> WithPatienceAsync<T>(Task<T> operation, string what)
    {
        if (await Task.WhenAny(operation, Task.Delay(OperationPatience)) != operation)
        {
            Log.Warn($"Windows audio has not responded for {OperationPatience.TotalSeconds:0} s while {what}.");
            _popups.Show(new AlertModel(
                "Windows audio is not responding",
                $"BlindsDown is still {what} your microphones and will finish as soon as Windows audio answers. Until then, don't rely on the lock.",
                AlertSeverity.Danger,
                Sticky: true,
                Actions: [new AlertAction("Open Sound settings", SafeShell.OpenSoundSettings)],
                GroupKey: "audio-not-responding"));
        }

        return await operation;
    }

    /// <summary>After locking: tell the user which apps already had a microphone or camera open (they now get
    /// silence or no picture). Apps on the "always allow" list are left out, so a permanent router like SteelSeries
    /// Sonar isn't repeated.</summary>
    private void ReportAppsAlreadyUsing(LockStatusInfo status)
    {
        var protectedIds = status.Devices
            .Where(d => d.Protection is DeviceProtection.HardwareMute or DeviceProtection.SoftwareMute or DeviceProtection.EndpointDisabled or DeviceProtection.DeviceDisabled)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (CaptureKind kind in new[] { CaptureKind.Microphone, CaptureKind.Camera })
        {
            List<string> apps = _activeUsage
                .Where(s => s.Kind == kind && protectedIds.Contains(s.DeviceId) && !_settings.IsAppAllowed(s.App.GroupKey))
                .Select(s => s.App.DisplayName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (apps.Count == 0)
            {
                continue;
            }

            string word = DeviceWord(kind);
            _popups.Show(new AlertModel(
                apps.Count == 1 ? $"{apps[0]} had your {word} open" : $"Apps had your {word} open",
                kind == CaptureKind.Camera ? $"{string.Join(", ", apps)} no longer get a picture." : $"{string.Join(", ", apps)} now only hear silence.",
                AlertSeverity.Info));
        }
    }

    /// <summary>"4 microphones and 1 camera locked", or what could not be locked.</summary>
    private static string LockedText(LockStatusInfo status)
    {
        if (status.State == LockState.PartiallyLocked)
        {
            return status.Problems.Count == 1 ? "1 device could not be locked" : $"{status.Problems.Count} devices could not be locked";
        }

        return CoveredText(status);
    }

    /// <summary>What the lock covers now ("5 microphones and 1 camera locked"), for a timeline update: after another
    /// program's change a partial lock isn't devices that "could not be locked".</summary>
    private static string CoveredText(LockStatusInfo status)
    {
        int mics = status.Devices.Count(d => d.Kind == DeviceKind.Microphone
            && d.Protection is DeviceProtection.HardwareMute or DeviceProtection.SoftwareMute or DeviceProtection.EndpointDisabled);
        int cameras = status.Devices.Count(d => d.Kind == DeviceKind.Camera && d.Protection == DeviceProtection.DeviceDisabled);
        string micText = mics == 1 ? "1 microphone" : $"{mics} microphones";
        return cameras == 0 ? micText + " locked" : $"{micText} and {(cameras == 1 ? "1 camera" : $"{cameras} cameras")} locked";
    }

    /// <param name="at">When it happened (a lock change the helper reports later); now when not given.</param>
    private void RecordLockEvent(LockEventAction action, LockEventSource source, string? summary, bool hadProblems, DateTimeOffset? at = null)
    {
        try
        {
            _lockEvents.Append(new LockEventRecord
            {
                At = at ?? DateTimeOffset.UtcNow,
                Action = action,
                Source = source,
                Summary = summary,
                HadProblems = hadProblems,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Writing the lock timeline failed", ex);
        }

        RefreshActivityWindow(reloadHistory: true);
    }

    private static string LogName(LockEventSource source) => source switch
    {
        LockEventSource.Hotkey => "hotkey",
        LockEventSource.Tray => "tray",
        LockEventSource.OtherProgram => "another program",
        LockEventSource.Automatic => "automatic",
        LockEventSource.Recovery => "recovery",
        LockEventSource.Exit => "exit",
        _ => source.ToString(),
    };

    /// <summary>Waits (briefly) until no lock or unlock is in progress.</summary>
    private async Task WaitUntilIdleAsync()
    {
        for (int i = 0; _busy && i < 300; i++)
        {
            await Task.Delay(100);
        }
    }

    private async Task HandleRecoveryAsync(StartupRecovery recovery)
    {
        if (recovery.ExitedCleanly)
        {
            _popups.Show(new AlertModel(
                "Still locked",
                $"Your devices have been locked since {recovery.LockedSince.ToLocalTime():g}. Press {_settings.GetHotkey()} or click the tray icon to unlock.",
                AlertSeverity.Info));
            if (_engine is LockEngine resumed && !_busy)
            {
                _busy = true;
                try
                {
                    string? error = await RelockHelperAfterStartAsync(resumed);
                    if (error is not null)
                    {
                        ShowHelperLockFailed(error);
                    }
                }
                finally
                {
                    _busy = false;
                    RecomputeStatus();
                }
            }

            return;
        }

        Log.Warn($"Recovering from an unclean exit while {recovery.Phase}");

        // No hotkey, tray click or command may lock or unlock while the user decides (commands from other programs
        // are queued until startup is done anyway).
        RecoveryChoice choice;
        _recovering = true;
        try
        {
            choice = Dialogs.AskRecovery(recovery, camerasLocked: _helper.HasLock);
        }
        finally
        {
            _recovering = false;

            // The user is back and has decided: a Win+L or sleep from while the question was open is over.
            if (_pendingAutoLock is not null)
            {
                Log.Info($"Automatic lock ({_pendingAutoLock}) dropped: it came before the crash-recovery answer.");
                _pendingAutoLock = null;
            }
        }

        if (choice == RecoveryChoice.Restore)
        {
            // A "Lock again" click (or a late operation) may still be running: the user's choice waits for it, never
            // gets lost.
            await WaitUntilIdleAsync();
            (_, string? error) = await UnlockAsync(LockEventSource.Recovery);
            if (error == StillBusy)
            {
                _popups.Show(new AlertModel(
                    "Not restored yet",
                    $"BlindsDown was still busy, so it didn't restore your devices yet. Press {_settings.GetHotkey()} or click the tray icon to unlock.",
                    AlertSeverity.Warning,
                    Sticky: true));
            }

            return;
        }

        if (_busy || _engine is not LockEngine engine)
        {
            return;
        }

        _busy = true;
        _transition = LockState.Locking;
        string? helperError = null;
        try
        {
            _micStatus = await WithPatienceAsync(_engineThread.InvokeAsync(engine.Lock), "locking"); // Finishes a half-done lock.
            helperError = await RelockHelperAfterStartAsync(engine);
            _transition = null;
            RecomputeStatus();
            // A helper that isn't answering yet at sign-in joins the lock in a moment (ResumeHelperLockWhenItAnswersAsync),
            // which updates this entry; until then it says what the microphones did.
            RecordLockEvent(
                LockEventAction.Lock,
                LockEventSource.Recovery,
                _resumeHelperLockPending ? CoveredText(_status) : LockedText(_status),
                (_resumeHelperLockPending ? _micStatus.State == LockState.PartiallyLocked : _status.State == LockState.PartiallyLocked) || helperError is not null);
        }
        catch (Exception ex)
        {
            Log.Error("Re-locking after the crash failed", ex);
            _popups.Show(new AlertModel("Lock failed", "BlindsDown could not lock your microphones: " + ex.Message, AlertSeverity.Danger, Sticky: true));
        }
        finally
        {
            _transition = null;
            _busy = false;
            RecomputeStatus();
        }

        if (helperError is not null)
        {
            ShowHelperLockFailed(helperError);
        }
    }

    /// <summary>
    /// The microphones are still locked from before this start: make sure the helper's part is there too. Sending the full
    /// request from the settings re-creates a lock that is gone and widens one another program narrowed (a request that
    /// is the same changes nothing). A lock this app knows the helper held (<see cref="HelperLink.HeldOwnLock"/>) that is
    /// gone without a change in the helper's log is announced like an unlock by another program; a change the log
    /// recorded was already announced (<see cref="OnHelperChanges"/>). Needs the helper to answer: the helper only opens
    /// its pipe after it has put its own lock back at boot, so at sign-in this check waits for it
    /// (<see cref="ResumeHelperLockWhenItAnswersAsync"/>).
    /// </summary>
    private async Task<string?> RelockHelperAfterStartAsync(LockEngine engine)
    {
        if (!WantsHelper(_settings) || !_helper.IsInstalled)
        {
            return null;
        }

        if (!_helper.CanLock && !_helper.IsChanging && _helper.State.Availability != HelperAvailability.NeedsUpdate)
        {
            if (!_resumeHelperLockPending)
            {
                _resumeHelperLockPending = true;
                _ = ResumeHelperLockWhenItAnswersAsync();
            }

            return null;
        }

        _resumeHelperLockPending = false;
        bool goneUnannounced = _helper.HeldOwnLock && _helper.CanLock && _helper.LockStateKnown && !_helper.HasLock && _foreignAlarm is null;
        string part = HelperHeldPart();
        string? error = await LockHelperAsync(engine, LogName(LockEventSource.Recovery));
        if (goneUnannounced)
        {
            Log.Warn("The camera helper had no lock at startup although it held this lock and the microphones were still locked.");
            AnnounceChangeElsewhere(
                $"{Capitalized(part)} {Was(part)} unlocked while BlindsDown wasn't running",
                error is null
                    ? $"Your {part} {Was(part)} no longer locked when BlindsDown started, and the camera helper has no record of who unlocked {Them(part)}. BlindsDown locked {Them(part)} again. If you didn't unlock {Them(part)}, check which programs are running."
                    : $"Your {part} {Was(part)} no longer locked when BlindsDown started, and locking {Them(part)} again didn't work: {error.TrimEnd('.')}.",
                DateTimeOffset.UtcNow,
                offerRelock: error is not null);
        }
        else if (error is null && _foreignAlarm is (IReadOnlyList<HelperChange> changes, bool beforeStart) && _helper.HasLock)
        {
            // The alarm about a change made while BlindsDown wasn't running stays, but its "lock again" is done.
            (string title, string message) = ForeignChangeText(changes, beforeStart, putBack: true);
            ShowForeignAlarm(title, message, offerRelock: false, ForeignSeverity(changes));
            _helperNarrowedElsewhere = false;
        }

        return error;
    }

    /// <summary>
    /// The microphones were still locked at this start but the helper didn't answer yet (it opens its pipe only after
    /// putting its own lock back): checks and re-sends its part as soon as it answers. Only after half a minute of
    /// silence does it say the cameras aren't locked; it keeps waiting after that, and the alert closes once the lock is in.
    /// </summary>
    private async Task ResumeHelperLockWhenItAnswersAsync()
    {
        DateTimeOffset warnAt = DateTimeOffset.UtcNow + HelperStartPatience;
        bool warned = false;
        while (_resumeHelperLockPending && !_exiting && !_disposed)
        {
            if (!_micStatus.IsLockedOrPartly)
            {
                _resumeHelperLockPending = false; // Unlocked meanwhile: nothing to resume.
                return;
            }

            if (_helper.CanLock && !_busy && !_recovering && !_exitPrompting && !_helperChanging && _engine is LockEngine engine)
            {
                bool alreadyHeld = _helper.HeldOwnLock && _helper.HasLock; // It put this lock back itself at boot.
                _busy = true;
                string? error;
                try
                {
                    error = await RelockHelperAfterStartAsync(engine);
                }
                finally
                {
                    _busy = false;
                    RecomputeStatus();
                }

                if (error is not null)
                {
                    ShowHelperLockFailed(error);
                }
                else if (_helper.HasLock)
                {
                    _popups.Dismiss("helper-lock-failed");
                    RecordLockEvent(LockEventAction.Update, LockEventSource.Recovery, CoveredText(_status) + (alreadyHeld ? string.Empty : " (the camera helper joined the lock)"), _status.State == LockState.PartiallyLocked);
                }

                return;
            }

            if (!warned && DateTimeOffset.UtcNow >= warnAt && !_helper.CanLock)
            {
                warned = true;
                ShowHelperLockFailed(_helper.State.Detail ?? "it isn't answering");
                RecordLockEvent(LockEventAction.Update, LockEventSource.Recovery, CoveredText(_status) + " (the camera helper didn't answer, so cameras aren't locked)", hadProblems: true);
            }

            await Task.Delay(1000);
        }
    }

    private void ShowHelperLockFailed(string error) =>
        _popups.Show(new AlertModel(
            "Cameras not locked",
            "The camera helper didn't lock your cameras: " + error.TrimEnd('.') + ". Your microphones are locked.",
            AlertSeverity.Danger,
            Sticky: true,
            Actions: [new AlertAction("Settings", () => ShowSettings("Camera"))],
            GroupKey: "helper-lock-failed"));

    // ---------------------------------------------------------------- status

    /// <summary>
    /// The lock state the user sees: the microphones' state merged with the helper's. Without a working helper,
    /// cameras are still listed (read-only, from this app) so Settings and alerts can say they aren't locked.
    /// </summary>
    private void RecomputeStatus()
    {
        AppSettings settings = _settings;
        var devices = new List<DeviceLockInfo>(_micStatus.Devices);
        var problems = new List<string>(_micStatus.Problems);
        DeviceLockStatus helper = _helper.DeviceStatus;
        bool helperListed = _helper.IsInstalled && helper.Devices.Count > 0;
        if (helperListed)
        {
            devices.AddRange(helper.Devices);
            problems.AddRange(helper.Problems);
        }
        else
        {
            devices.AddRange(_localCameras.Where(c => c.Kind == DeviceKind.Camera).Select(c => LocalCameraInfo(c, settings)));
        }

        LockState state = _transition ?? MergeState(_micStatus.State, _helper.IsInstalled ? helper.State : LockState.Unlocked);
        bool helperExpected = WantsHelper(settings) && _helper.IsInstalled;
        if (_transition is null && _micStatus.State == LockState.Unlocked && _helper.HasLock && !_helper.CanLock)
        {
            // The helper held a lock when it last answered, but it can't take a request now: nothing can unlock it, so the
            // tray offers what a press does (lock the microphones, see ToggleAsync) and says why.
            state = LockState.Unlocked;
            string held = HelperHeldPart();
            problems.Add(_helper.State.Availability == HelperAvailability.NeedsUpdate
                ? $"The camera helper can't take requests from this BlindsDown, and it still has your {held} turned off. Update it in Settings > Camera, then unlock."
                : $"The camera helper isn't answering. It may still have your {held} turned off; unlock once it answers.");
        }

        string helperPart = HelperPart(settings.LockCameras, settings.DisableMicOnlyHardware);
        if (state is LockState.Locked or LockState.PartiallyLocked && _micStatus.State != LockState.Unlocked && helperExpected && !_helper.HasLock)
        {
            state = LockState.PartiallyLocked;
            problems.Add(_helper.CanLock
                ? $"{char.ToUpperInvariant(helperPart[0])}{helperPart[1..]} {Verb(helperPart)} not locked."
                : $"The camera helper isn't answering, so {helperPart} {Verb(helperPart)} not locked.");
        }

        if (_helperNarrowedElsewhere && _helper.HasLock && state is LockState.Locked or LockState.PartiallyLocked)
        {
            state = LockState.PartiallyLocked;
            problems.Add("Another program changed the camera lock, so it may not cover everything you chose. Use \"Lock again\" in its popup, or unlock and lock again.");
        }

        if (state is LockState.Locked && _helper.IsInstalled && _helper.HasLock && _helper.State.Availability == HelperAvailability.NotResponding)
        {
            // Its devices most likely stay off, but nobody guards them or locks a camera plugged in now.
            state = LockState.PartiallyLocked;
            problems.Add("The camera helper isn't answering, so a camera connected or turned back on now isn't locked.");
        }

        if (_micStatus.State == LockState.Unlocked && _helper.HasLock && _helper.CanLock)
        {
            bool cameras = helper.Devices.Any(d => d.Kind == DeviceKind.Camera && d.Protection == DeviceProtection.DeviceDisabled);
            bool hardware = helper.Devices.Any(d => d.Kind == DeviceKind.MicrophoneHardware && d.Protection == DeviceProtection.DeviceDisabled);
            string part = HelperPart(cameras || !hardware, hardware);
            problems.Add($"Only {part} {Verb(part)} locked; your microphones are not.");
        }

        ApplyStatus(new LockStatusInfo(state, devices, problems));
    }

    /// <summary>"cameras", "microphone hardware" or "cameras and microphone hardware".</summary>
    private static string HelperPart(bool cameras, bool hardware) =>
        cameras && hardware ? "cameras and microphone hardware" : hardware ? "microphone hardware" : "cameras";

    private static string Verb(string helperPart) => helperPart == "microphone hardware" ? "is" : "are";

    private static string Was(string helperPart) => helperPart == "microphone hardware" ? "was" : "were";

    private static string Them(string helperPart) => helperPart == "microphone hardware" ? "it" : "them";

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    internal static LockState MergeState(LockState microphones, LockState helper)
    {
        if (microphones is LockState.Locking || helper is LockState.Locking)
        {
            return LockState.Locking;
        }

        if (microphones is LockState.Unlocking || helper is LockState.Unlocking)
        {
            return LockState.Unlocking;
        }

        return (microphones, helper) switch
        {
            (LockState.Unlocked, LockState.Unlocked) => LockState.Unlocked,
            (LockState.Locked, LockState.Locked or LockState.Unlocked) => LockState.Locked,
            _ => LockState.PartiallyLocked,
        };
    }

    private static DeviceLockInfo LocalCameraInfo(PnpDevice camera, AppSettings settings) => new(
        camera.InstanceId,
        camera.Name,
        camera.StableKey,
        DeviceKind.Camera,
        camera.IsVirtual,
        HasHardwareMute: false,
        !camera.IsPresent ? DeviceProtection.NotPresent
            : camera.IsDisabled ? DeviceProtection.DisabledByUser
            : settings.LockCameras ? DeviceProtection.NeedsHelper
            : DeviceProtection.None,
        Problem: null,
        camera.CameraType);

    private void ApplyStatus(LockStatusInfo status)
    {
        _status = status;
        _tray.SetStatus(status, _settings.GetHotkey(), CameraLockOn);
        _overlay.Update(status.State, _settings, CamerasLocked(status));
        _helper.SetFastPolling(status.State != LockState.Unlocked || _settingsOpen);
    }

    /// <summary>Cameras are part of the lock: the helper answers and the user wants cameras locked.</summary>
    private bool CameraLockOn => _helper.CanLock && _settings.LockCameras;

    private static bool CamerasLocked(LockStatusInfo status) =>
        status.Devices.Any(d => d.Kind == DeviceKind.Camera && d.Protection == DeviceProtection.DeviceDisabled);

    private void OnHelperChanged()
    {
        // The helper disappeared (removed from Installed apps by an administrator, say) while it held this lock: it turned
        // the devices back on first. Never quietly.
        CheckHelperRemoved();

        if (_helper.State.Availability == HelperAvailability.NeedsUpdate && HelperLink.CompareToApp(_helper.State.HelperVersion) < 0
            && !_updateOffered && !_helperChanging)
        {
            // Settings is the owner of the permission prompt, so it can't end up hidden behind other windows.
            _updateOffered = true;
            _popups.Show(new AlertModel(
                "Update the camera helper",
                "The camera helper is from an older version of BlindsDown. Update it in Settings > Camera so camera locking keeps working.",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [new AlertAction("Settings", () => ShowSettings("Camera"))],
                GroupKey: "helper-update"));
        }

        RecomputeStatus();

        // The device list is only rebuilt when the helper came or went, so unsaved exclusions in Settings survive polls.
        bool availabilityChanged = _helper.State.Availability != _lastHelperAvailability;
        _lastHelperAvailability = _helper.State.Availability;
        _openSettings?.SetHelperState(_helper.State, availabilityChanged ? _status.Devices : null);
        if (_helper.CanLock)
        {
            _ = KeepHardwareOnAsync();
        }
    }

    /// <summary>The helper holds a lock that is this user's: one this app made, or one in force while the microphones are
    /// locked (the helper is shared by every account on the PC; another account's lock is not this one's).</summary>
    private bool HelperHoldsThisLock() => _helper.HasLock && (_helper.HeldOwnLock || _micStatus.IsLockedOrPartly);

    /// <summary>
    /// The helper disappeared (removed from Installed apps by an administrator, say) while it held this user's lock: it
    /// turned the devices back on first. Never quietly, also when that happened while BlindsDown wasn't running (the
    /// first check at startup, from <see cref="HelperLink.HeldOwnLock"/>). Needs Windows to have said the helper isn't
    /// installed; until the helper has answered, what it held is not known, so the last known answer counts.
    /// </summary>
    private void CheckHelperRemoved()
    {
        bool installed = _helper.IsInstalled;
        if (_helperHadLock && !installed && _helper.InstallStateKnown && !_helperChanging && !_busy)
        {
            string part = HelperPart(_settings.LockCameras || !_settings.DisableMicOnlyHardware, _settings.DisableMicOnlyHardware);
            bool whileClosed = !_engineReady; // The first check at startup.
            Log.Warn("The camera helper was removed while it held the lock" + (whileClosed ? " (while BlindsDown wasn't running)." : "."));
            AnnounceChangeElsewhere(
                "Camera helper removed",
                whileClosed
                    ? $"The camera helper was removed while BlindsDown wasn't running. It held your lock, so your {part} {Verb(part)} on again. If that wasn't you, check which programs are running."
                    : $"The camera helper was removed at {DateTime.Now:t} while your {part} {Was(part)} locked, so {Them(part)} {(Them(part) == "it" ? "is" : "are")} on again. If that wasn't you, check which programs are running.",
                DateTimeOffset.UtcNow,
                offerRelock: false);
            _helper.ForgetHeldLock();
            _helperHadLock = false;
            return;
        }

        if (!installed && _helper.InstallStateKnown)
        {
            _helperHadLock = false;
        }
        else if (installed && _helper.LockStateKnown)
        {
            _helperHadLock = HelperHoldsThisLock();
        }
        else
        {
            // Installed but not answered yet (or Windows couldn't be asked): keep what was known.
            _helperHadLock = _helperHadLock || _helper.HeldOwnLock;
        }
    }

    /// <summary>
    /// The changes to the helper's lock in one of its answers. Every Lock and Unlock this app sends carries an ID of its
    /// own; a change with another ID came from another program (any program running as a signed-in user can ask the
    /// helper), also when it happened while BlindsDown wasn't running. When it affects this user's lock (the helper held
    /// it, or the microphones are locked with cameras wanted), an unlock or a lock that covers less is never quiet: the
    /// unlock tone (even with sounds off), the on-screen message, a red popup that stays, and the lock timeline, once for
    /// the whole answer and with the times the helper recorded. A change to another account's lock is only logged.
    /// </summary>
    private void OnHelperChanges(HelperChangeBatch batch)
    {
        // Per change: this user's lock is affected while the helper held it (as far as this app knew at that point in the
        // answer), or while the microphones were locked with the helper wanted.
        bool wanted = _micStatus.IsLockedOrPartly && WantsHelper(_settings);
        bool inForce = batch.HeldBefore;
        var foreign = new List<HelperChange>();
        var unchecked_ = new List<HelperChange>();
        bool anyForeign = false;
        bool ownLockSince = false;
        foreach ((HelperChange change, bool own) in batch.Changes)
        {
            if (own)
            {
                // A late answer to this app's own request (it timed out, then the helper finished): the warning is over.
                if (change.Kind == HelperChangeKind.Unlocked)
                {
                    _popups.Dismiss("helper-unlock-failed");
                }
                else if (change.Kind == HelperChangeKind.Locked)
                {
                    _popups.Dismiss("helper-lock-failed");
                    if (_engine is LockEngine engine)
                    {
                        _engineThread.Post(engine.NoteDefaultsSettled);
                    }
                }

                inForce = change.Kind != HelperChangeKind.Unlocked;
                ownLockSince = inForce;
                continue;
            }

            anyForeign = true;
            if (_helper.StateUnreadable && change.At < _startedAt)
            {
                // helper-state.txt couldn't be read: whether this was BlindsDown's own change can't be told, so it is
                // said as that, not as another program's.
                unchecked_.Add(change);
                continue;
            }

            bool affects = inForce || (wanted && change.At >= _micsLockedSince);
            Log.Warn($"Camera helper: lock change \"{change.Kind}\" at {change.At:u} by another program (it said it was \"{change.Source}\")"
                + (affects ? string.Empty : ", not to this user's lock") + $". Turned back on: {string.Join(", ", change.TurnedOn)}; no longer covered: {string.Join(", ", change.Released)}.");
            if (affects)
            {
                foreign.Add(change);
                ownLockSince = false;
            }

            if (change.Kind == HelperChangeKind.Unlocked)
            {
                inForce = false;
            }
        }

        if (unchecked_.Count > 0)
        {
            ShowUncheckedChanges(unchecked_);
        }

        if (foreign.Count == 0)
        {
            if (anyForeign)
            {
                RecomputeStatus();
            }

            return;
        }

        // The lock in force now was set (or narrowed) by another program: it may not cover what the user chose.
        _helperNarrowedElsewhere = !ownLockSince && _helper.HasLock;
        bool beforeStart = foreign[0].At < _startedAt;
        (string title, string message) = ForeignChangeText(foreign, beforeStart, putBack: ownLockSince);
        if (foreign.All(c => c.Kind == HelperChangeKind.Locked))
        {
            // Only "locked" by another program: no device was turned back on (no unlock tone), but the lock in force isn't
            // the one the user chose, so it says so and offers to put that back. Its own popup: a red unlock alarm still
            // open stays as it is.
            _foreignAlarm = (foreign, beforeStart);
            RecomputeStatus();
            ShowForeignAlarm(title, message, offerRelock: !ownLockSince, ForeignSeverity(foreign));
            return;
        }

        _foreignAlarm = (foreign, beforeStart);
        AnnounceChangeElsewhere(title, message, foreign[^1].At, offerRelock: !ownLockSince);
    }

    /// <summary>Changes from before this start that can't be told apart from BlindsDown's own (helper-state.txt couldn't be
    /// read): an amber notice that says what changed and that it couldn't be checked.</summary>
    private void ShowUncheckedChanges(IReadOnlyList<HelperChange> changes)
    {
        _uncheckedNoticeShown = true;
        List<string> turnedOn = [.. changes.SelectMany(c => c.TurnedOn).Distinct(StringComparer.OrdinalIgnoreCase)];
        string at = changes[^1].At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string what = turnedOn.Count > 0 ? $" {DeviceNames(turnedOn)} {(turnedOn.Count == 1 ? "was" : "were")} turned back on." : string.Empty;
        _popups.Show(new AlertModel(
            "Couldn't check the camera lock's history",
            $"The camera lock changed while BlindsDown wasn't running (last at {at}).{what} BlindsDown couldn't read its file %LocalAppData%\\BlindsDown\\helper-state.txt, so it can't tell whether that was you. Changes from now on are checked as usual.",
            AlertSeverity.Warning,
            Sticky: true,
            GroupKey: "helper-state-unreadable"));
    }

    /// <summary>Red when another program turned devices back on or ended the lock; amber when it only set a lock.</summary>
    private static AlertSeverity ForeignSeverity(IReadOnlyList<HelperChange> changes) =>
        changes.All(c => c.Kind == HelperChangeKind.Locked) ? AlertSeverity.Warning : AlertSeverity.Danger;

    /// <summary>The alarm text for lock changes by another program (one answer's worth), with the helper's times.</summary>
    /// <param name="beforeStart">The first change happened before this run of BlindsDown started.</param>
    /// <param name="putBack">This app has locked again since (its own lock came after them).</param>
    internal static (string Title, string Message) ForeignChangeText(IReadOnlyList<HelperChange> changes, bool beforeStart, bool putBack)
    {
        HelperChange last = changes[^1];
        bool unlocked = changes.Any(c => c.Kind == HelperChangeKind.Unlocked);
        bool onlyLocked = changes.All(c => c.Kind == HelperChangeKind.Locked);
        List<string> turnedOn = [.. changes.SelectMany(c => c.TurnedOn).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> released = [.. changes.SelectMany(c => c.Released).Distinct(StringComparer.OrdinalIgnoreCase).Except(turnedOn, StringComparer.OrdinalIgnoreCase)];
        string at = last.At.ToLocalTime().ToString(beforeStart ? "g" : "t", CultureInfo.CurrentCulture);
        string when = beforeStart ? $"While BlindsDown wasn't running ({at})" : $"At {at}";
        string what = changes.Count > 1
            ? $"other programs changed the camera helper's lock {changes.Count} times"
            : unlocked ? "another program told the camera helper to unlock"
            : onlyLocked ? "another program set the camera helper's lock"
            : "another program told the camera helper to lock less";
        string result = (turnedOn.Count, released.Count) switch
        {
            (0, 0) when onlyLocked && putBack => "so for a while the lock in force wasn't the one you chose",
            (0, 0) when onlyLocked => "so the lock in force may not cover everything you chose",
            (0, 0) => unlocked ? "so the lock ended (none of the devices it covered was off at that moment)" : "so the lock covers less",
            (0, _) => $"so the lock no longer covers {DeviceNames(released)}",
            (_, 0) => $"so {DeviceNames(turnedOn)} {(turnedOn.Count == 1 ? "was" : "were")} turned back on",
            _ => $"so {DeviceNames(turnedOn)} {(turnedOn.Count == 1 ? "was" : "were")} turned back on and the lock no longer covers {DeviceNames(released)}",
        };
        string relocked = !onlyLocked && last.Kind == HelperChangeKind.Locked && !putBack
            ? " Then another program locked again, and its lock may not cover everything you chose."
            : string.Empty;
        string advice = putBack
            ? " BlindsDown has locked again since. If that wasn't you, check which programs are running."
            : " If that wasn't you, lock again and check which programs are running.";
        string title = unlocked ? "Unlocked by another program" : "Lock changed by another program";
        return (title, $"{when}, {what}, {result}.{relocked}{advice}");
    }

    /// <summary>"Camera", "Camera and Headset", "Camera, Headset and Dock", or "5 devices (A, B, C, …)".</summary>
    internal static string DeviceNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "no devices",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => $"{names.Count} devices ({string.Join(", ", names.Take(3))}, …)",
    };

    /// <summary>The alarm for devices turned back on, or no longer locked, by something other than this app.</summary>
    /// <param name="at">When it happened (from the helper's log), for the timeline.</param>
    /// <param name="offerRelock">Offer "Lock again" (while the microphones are locked).</param>
    private void AnnounceChangeElsewhere(string title, string message, DateTimeOffset at, bool offerRelock)
    {
        RecomputeStatus();
        _sounds.Play(SoundCue.Unlock);
        _osd.Show(_status.State, title, cameras: CamerasLocked(_status));
        ShowForeignAlarm(title, message, offerRelock, AlertSeverity.Danger);
        if (_micStatus.IsLockedOrPartly)
        {
            // The microphones stay locked, so the lock goes on: its timeline entry says what changed.
            RecordLockEvent(LockEventAction.Update, LockEventSource.OtherProgram, $"{CoveredText(_status)}. {title}", hadProblems: true, at);
        }
        else
        {
            RecordLockEvent(LockEventAction.Unlock, LockEventSource.OtherProgram, title + ". " + message, hadProblems: true, at);
        }
    }

    private void ShowForeignAlarm(string title, string message, bool offerRelock, AlertSeverity severity = AlertSeverity.Danger)
    {
        _foreignAlarmsShown++;
        var model = new AlertModel(
            title,
            message,
            severity,
            Sticky: true,
            Actions: offerRelock && _micStatus.IsLockedOrPartly && _helper.IsInstalled ? [new AlertAction("Lock again", () => _ = RelockHelperAsync())] : null,
            GroupKey: severity == AlertSeverity.Danger ? "helper-unlocked-by-other" : "helper-locked-by-other");
        _lastForeignAlarm = model;
        _popups.Show(model);
    }

    /// <summary>"Lock again" after another program unlocked or narrowed the helper's lock. Its button closes the alarm, so
    /// every way it doesn't put the lock back shows the alarm again and says why.</summary>
    private async Task RelockHelperAsync()
    {
        if (_exiting || _engine is not LockEngine engine)
        {
            return;
        }

        string? notNow = _busy || _recovering || _exitPrompting ? "BlindsDown is busy with another lock, unlock or question. Try again in a moment."
            : _helperChanging ? "The camera helper is being set up. Try again when that is done."
            : !_micStatus.IsLockedOrPartly ? "Your microphones aren't locked any more. Lock again with the hotkey or the tray icon."
            : !_helper.IsInstalled ? "The camera helper isn't installed any more."
            : !WantsHelper(_settings) ? "Cameras aren't part of the lock any more (Settings > Camera)."
            : _helper.IsChanging ? "The camera helper isn't answering yet. Try again in a moment."
            : null;
        if (notNow is not null)
        {
            ReshowForeignAlarmLater();
            _popups.Show(new AlertModel("Not locked again", notNow, AlertSeverity.Warning, GroupKey: "relock-not-now"));
            return;
        }

        _busy = true;
        try
        {
            string? error = await LockHelperAsync(engine, "lock again");
            RecomputeStatus();
            if (error is null && !_helper.HasLock)
            {
                // Nothing was sent (the helper is being set up, or cameras aren't wanted any more), or it took nothing.
                error = _helper.IsChanging || _helperChanging ? "the camera helper is being set up; try again when that is done" : "the camera helper didn't take the lock";
            }

            if (error is not null)
            {
                ShowHelperLockFailed(error);
                ReshowForeignAlarmLater(); // Its button closed the alarm: it stays until the lock is really back.
            }
            else
            {
                _popups.Dismiss("helper-unlocked-by-other");
                _popups.Dismiss("helper-locked-by-other");
                _foreignAlarm = null;
                _lastForeignAlarm = null;
                _osd.Show(_status.State, LockedText(_status), cameras: CamerasLocked(_status));
                RecordLockEvent(LockEventAction.Update, LockEventSource.Tray, CoveredText(_status) + " (locked again after another program changed the lock)", _status.State == LockState.PartiallyLocked);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Shows the alarm about another program again once the popup whose button was pressed has closed.</summary>
    private void ReshowForeignAlarmLater() => BeginOnUi(() =>
    {
        if (_lastForeignAlarm is AlertModel model)
        {
            _popups.Show(model);
        }
    });

    /// <summary>Lists cameras without the helper (read-only), so the user sees them as "not locked".</summary>
    private async Task RefreshLocalCamerasAsync()
    {
        try
        {
            _localCameras = await _engineThread.InvokeAsync(() => (IReadOnlyList<PnpDevice>)new CfgMgrPnpBackend().Enumerate());
        }
        catch (Exception ex)
        {
            Log.Error("Listing cameras failed", ex);
        }

        RecomputeStatus();
    }

    /// <summary>The helper's own alerts. The one about a lock change that left devices out comes with the change it belongs
    /// to (<see cref="OnHelperChanges"/>, which says who made it), so <see cref="HelperLink"/> passes it on only when the
    /// change isn't in the same answer.</summary>
    private void OnHelperEvent(HelperEvent helperEvent)
    {
        ShowEngineAlert(new EngineAlert(helperEvent.Kind, helperEvent.DeviceName, helperEvent.Message, helperEvent.Urgent), fromHelper: true);
    }

    private void ShowEngineAlert(EngineAlert alert, bool fromHelper)
    {
        Log.Info($"Alert {alert.Kind}{(fromHelper ? " (helper)" : string.Empty)}: {alert.Message}");
        string thing = fromHelper ? "device" : "microphone";
        string capitalThing = fromHelper ? "Device" : "Microphone";
        (string title, AlertSeverity severity, bool sticky) = alert.Kind switch
        {
            EngineAlertKind.UnmutedWhileLocked => ("Microphone unmuted while locked", AlertSeverity.Danger, true),
            EngineAlertKind.ReEnabledWhileLocked => ($"{capitalThing} turned back on while locked", AlertSeverity.Danger, true),
            EngineAlertKind.Tampering => (fromHelper ? "Something keeps turning a device back on" : "Something keeps undoing the microphone lock", AlertSeverity.Danger, true),
            EngineAlertKind.LockFailed when alert.DeviceName is "lock record cleanup" => ("BlindsDown couldn't delete an old lock record", AlertSeverity.Warning, true),
            EngineAlertKind.LockFailed when alert.DeviceName is "restore list" or "lock record" => ("BlindsDown can't save its records", AlertSeverity.Danger, true),
            EngineAlertKind.LockFailed => ($"A {thing} could not be locked", AlertSeverity.Danger, true),
            EngineAlertKind.DeviceBusy => ("A device is in use", AlertSeverity.Warning, true),
            EngineAlertKind.NewDeviceLocked => ($"New {thing} locked", AlertSeverity.Warning, true),
            EngineAlertKind.DeviceRelocked => ($"{capitalThing} locked again", AlertSeverity.Warning, true),
            EngineAlertKind.StrongerLockUnavailable => ("Microphones muted instead of turned off", AlertSeverity.Info, false),
            EngineAlertKind.DeferredRestoreDone => ($"{capitalThing} restored", AlertSeverity.Success, false),
            _ => ("BlindsDown", AlertSeverity.Info, false),
        };

        string source = fromHelper ? "helper" : "engine";
        bool isNew = _popups.Show(new AlertModel(title, alert.Message, severity, sticky, GroupKey: $"{source}|{alert.Kind}|{alert.DeviceName}"));
        if (isNew && alert.IsUrgent && _settings.PlaySounds)
        {
            _sounds.Play(SoundCue.Alert);
        }
    }

    // ---------------------------------------------------------------- usage

    /// <summary>Runs on the engine thread.</summary>
    private void ReportMicSessions()
    {
        if (_sessions is null || _tracker is null)
        {
            return;
        }

        try
        {
            _tracker.Report(MicSessionMonitor.SourceName, _sessions.GetActiveSignals());
        }
        catch (Exception ex)
        {
            Log.Error("Reading audio sessions failed", ex);
        }

        PublishActiveUsage();
    }

    /// <summary>Runs on the engine thread.</summary>
    private void ReportCameraSessions()
    {
        if (_cameras is null || _tracker is null || !_cameras.IsAvailable)
        {
            return;
        }

        try
        {
            _tracker.Report(CameraActivityMonitor.SourceName, _cameras.GetActiveSignals());
        }
        catch (Exception ex)
        {
            Log.Error("Reading camera activity failed", ex);
        }

        PublishActiveUsage();
    }

    /// <summary>Runs on the engine thread.</summary>
    private void ReportUsageRegistry()
    {
        if (_consent is null || _tracker is null)
        {
            return;
        }

        try
        {
            _tracker.Report(
                ConsentStoreMonitor.SourceName,
                _consent.GetActiveSignals(CaptureKind.Microphone).Concat(_consent.GetActiveSignals(CaptureKind.Camera)));
        }
        catch (Exception ex)
        {
            Log.Error("Reading the usage registry failed", ex);
        }

        PublishActiveUsage();
    }

    /// <summary>Runs on the engine thread.</summary>
    private void PublishActiveUsage()
    {
        if (_tracker is null)
        {
            return;
        }

        IReadOnlyList<UsageSession> active = _tracker.ActiveSessions;
        OnUi(() =>
        {
            _activeUsage = active;
            _tray.SetActiveUsage(active);
            RefreshActivityWindow(reloadHistory: false);
        });
    }

    /// <summary>Runs on the engine thread.</summary>
    private void OnUsageEnded(UsageSession session)
    {
        AppSettings settings = _settings;
        if (session.End is not DateTimeOffset end || _tracker is null || end - session.Start < _tracker.ConfirmAfter)
        {
            // Apps often open a device for a split second just to list it; that's not "using" it.
            PublishActiveUsage();
            return;
        }

        if (settings.HistoryEnabled)
        {
            try
            {
                _history.Append(UsageRecord.From(session, end));
            }
            catch (IOException ex)
            {
                Log.Error("Writing history failed", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Writing history failed", ex);
            }
        }

        PublishActiveUsage();
        OnUi(() =>
        {
            RefreshActivityWindow(reloadHistory: true);
            if (_settings.AlertsEnabled && _settings.AlertOnStop && !_settings.IsAppAllowed(session.App.GroupKey))
            {
                TimeSpan duration = session.Duration(DateTimeOffset.UtcNow);
                _popups.Show(new AlertModel(
                    $"{session.App.DisplayName} stopped using your {DeviceWord(session.Kind)}",
                    $"Used for {FormatDuration(duration)}.",
                    AlertSeverity.Info));
            }
        });
    }

    private void OnUsageConfirmed(UsageSession session)
    {
        // While locked, every app that opens a locked kind of device is shown, even apps on the "always allow" list
        // and even with popups switched off: that is exactly when the user wants to know. Cameras count as locked
        // only when the user wants them locked.
        bool locked = _status.IsLockedOrPartly && (session.Kind == CaptureKind.Microphone || _settings.LockCameras);
        if (!locked && (!_settings.AlertsEnabled || _settings.IsAppAllowed(session.App.GroupKey)))
        {
            return;
        }

        string key = session.Kind + "|" + session.App.GroupKey;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!locked && _lastUsageAlert.TryGetValue(key, out DateTimeOffset last) && now - last < UsageAlertRepeat)
        {
            return;
        }

        _lastUsageAlert[key] = now;
        string app = session.App.DisplayName;
        string word = DeviceWord(session.Kind);
        string device = session.ViaApp is null ? session.DeviceName : $"{session.DeviceName}, a virtual microphone from {session.ViaApp}";
        string browserNote = BrowserExes.Contains(Path.GetFileName(session.App.ExePath) ?? string.Empty)
            ? " Windows doesn't say which website."
            : string.Empty;
        AlertAction allow = new("Always allow", () => AllowApp(session.App));
        AlertAction details = new("Activity", ShowActivity);

        if (!locked)
        {
            _popups.Show(new AlertModel(
                $"{app} is using your {word}",
                device + "." + browserNote,
                AlertSeverity.Info,
                Actions: [allow, details],
                GroupKey: "usage|" + key));
            return;
        }

        // Locked: say exactly what this device's protection is. Never claim "silence" for a device that is excluded,
        // failed to lock, or isn't known.
        DeviceKind kind = session.Kind == CaptureKind.Camera ? DeviceKind.Camera : DeviceKind.Microphone;
        DeviceLockInfo? info = _status.Devices.FirstOrDefault(d => d.Kind == kind && string.Equals(d.Id, session.DeviceId, StringComparison.OrdinalIgnoreCase));
        string groupKey = $"usage-locked|{session.App.GroupKey}|{session.DeviceId}";
        AlertModel model = session.Kind == CaptureKind.Camera
            ? CameraAlertWhileLocked(app, device, info, session, groupKey, details, browserNote)
            : info?.Protection switch
            {
                DeviceProtection.EndpointDisabled => new AlertModel(
                    $"{app} opened your microphone",
                    $"Your microphones are locked: {device} is turned off in Windows, so {app} can't use it, and an app that already had it open only hears silence.{browserNote}",
                    AlertSeverity.Warning,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
                DeviceProtection.HardwareMute => new AlertModel(
                    $"{app} opened your microphone",
                    $"Your microphones are locked, so {app} only hears silence. Device: {device}.{browserNote}",
                    AlertSeverity.Warning,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
                DeviceProtection.SoftwareMute => new AlertModel(
                    $"{app} opened your microphone",
                    $"Your microphones are locked, so {app} should only hear silence. Device: {device}. This mic is muted in software, which an app using exclusive mode could bypass.{browserNote}",
                    AlertSeverity.Warning,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
                DeviceProtection.Excluded => new AlertModel(
                    $"{app} is using a microphone you excluded",
                    $"{device} is not part of the lock, so {app} can hear you.{browserNote}",
                    AlertSeverity.Danger,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
                null when string.IsNullOrEmpty(session.DeviceId) => new AlertModel(
                    $"{app} is using a microphone",
                    $"Your microphones are locked. Windows didn't say which microphone {app} opened; check Settings > Devices that every microphone is protected.{browserNote}",
                    AlertSeverity.Warning,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
                _ => new AlertModel(
                    $"{app} is using an unprotected microphone",
                    $"{device} could not be locked, so {app} can hear you.{browserNote}",
                    AlertSeverity.Danger,
                    Sticky: true,
                    Actions: [details],
                    GroupKey: groupKey),
            };

        bool isNew = _popups.Show(model);
        if (isNew && model.Severity == AlertSeverity.Danger && _settings.PlaySounds)
        {
            _sounds.Play(SoundCue.Alert);
        }
    }

    private AlertModel CameraAlertWhileLocked(string app, string device, DeviceLockInfo? camera, UsageSession session, string groupKey, AlertAction details, string browserNote)
    {
        if (!_helper.IsInstalled || camera?.Protection == DeviceProtection.NeedsHelper)
        {
            return new AlertModel(
                $"{app} is using your camera",
                $"{device}. Your microphones are locked, but cameras need the camera helper to be locked. Set it up in Settings > Camera.{browserNote}",
                AlertSeverity.Danger,
                Sticky: true,
                Actions: [new AlertAction("Set up", () => ShowSettings("Camera")), details],
                GroupKey: groupKey);
        }

        return camera?.Protection switch
        {
            DeviceProtection.DeviceDisabled => new AlertModel(
                $"{app} tried your camera",
                $"Your cameras are locked, so {app} gets no picture. Device: {device}.{browserNote}",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [details],
                GroupKey: groupKey),
            DeviceProtection.Excluded => new AlertModel(
                $"{app} is using a camera you excluded",
                $"{device} is not part of the lock, so {app} can see you.{browserNote}",
                AlertSeverity.Danger,
                Sticky: true,
                Actions: [details],
                GroupKey: groupKey),
            null when string.IsNullOrEmpty(session.DeviceId) => new AlertModel(
                $"{app} is using a camera",
                $"Windows didn't say which camera {app} opened; check Settings > Devices that every camera is locked.{browserNote}",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [details],
                GroupKey: groupKey),
            _ => new AlertModel(
                $"{app} is using an unlocked camera",
                $"{device} is not locked{(camera?.Problem is string problem ? " (" + problem.TrimEnd('.') + ")" : string.Empty)}, so {app} can see you.{browserNote}",
                AlertSeverity.Danger,
                Sticky: true,
                Actions: [details],
                GroupKey: groupKey),
        };
    }

    private void AllowApp(AppIdentity app)
    {
        AppSettings updated = SettingsStore.Clone(_settings);
        if (!updated.IsAppAllowed(app.GroupKey))
        {
            updated.AllowedApps.Add(new AllowedApp { AppKey = app.GroupKey, DisplayName = app.DisplayName });
        }

        UpdateSettings(updated);
        _popups.Show(new AlertModel(
            $"No more popups for {app.DisplayName} while unlocked",
            "While locked you still see a popup if it opens a locked device. Its use still appears in Activity. You can undo this in Settings > Alerts.",
            AlertSeverity.Success));
    }

    // ---------------------------------------------------------------- windows

    private void ShowActivity()
    {
        if (_activity is null || _activity.IsDisposed)
        {
            _activity = new ActivityForm();
            _activity.AllowAppRequested += AllowApp;
            _activity.FormClosed += (_, _) => _activity = null;
        }

        RefreshActivityWindow(reloadHistory: true);
        _activity.Show();
        if (_activity.WindowState == FormWindowState.Minimized)
        {
            _activity.WindowState = FormWindowState.Normal;
        }

        _activity.Activate();
    }

    private void RefreshActivityWindow(bool reloadHistory)
    {
        if (_activity is null || _activity.IsDisposed)
        {
            return;
        }

        if (reloadHistory)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.HistoryRetentionDays);
            try
            {
                _historyCache = _history.ReadAll().Where(r => r.End >= cutoff).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Reading history failed", ex);
            }

            try
            {
                _lockEventCache = _lockEvents.ReadAll().Where(e => e.At >= cutoff).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Reading the lock timeline failed", ex);
            }
        }

        _activity.SetData(_activeUsage, _historyCache, _lockEventCache);
    }

    private void ShowSettings(string? tab = null) => ShowSettings(tab, installHelper: false);

    /// <param name="tab">The tab to show.</param>
    /// <param name="installHelper">Start installing the helper as soon as Settings is on screen (the user already chose it
    /// in the welcome window): Settings owns the permission prompt and shows the progress and the result.</param>
    private void ShowSettings(string? tab, bool installHelper)
    {
        if (_settingsOpen)
        {
            _openSettings?.SelectTab(tab ?? "General");
            _openSettings?.Activate();
            return;
        }

        _ = ShowSettingsAsync(tab, installHelper);
    }

    private async Task ShowSettingsAsync(string? tab, bool installHelper)
    {
        // The global hotkey stays registered while Settings is open (it may sit behind other windows for a long time);
        // a press is only redirected to the hotkey box while that box is taking keystrokes (see OnHotkeyPressed).
        _settingsOpen = true;
        bool lockedAtOpen = _status.State != LockState.Unlocked;
        var allowedAtOpen = _settings.AllowedApps.Select(a => a.AppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Fresh device lists: cameras may have been plugged in since the last look.
            if (!_helper.CanLock)
            {
                await RefreshLocalCamerasAsync();
            }

            await _helper.RefreshAsync();
            _helper.SetFastPolling(true);
            using var form = new SettingsForm(
                SettingsStore.Clone(_settings),
                _status.Devices,
                locked: _status.State != LockState.Unlocked,
                startWithWindows: SafeAutostartEnabled(),
                tryHotkey: TestHotkey,
                startupDisabledInTaskManager: SafeAutostartDisabledInTaskManager(),
                helper: _helper.State,
                isLockedNow: () => _status.State != LockState.Unlocked);
            form.ClearHistoryRequested += ClearHistory;

            // The form has already asked the user (and explained what gets installed) before raising these.
            form.EnableHelperRequested += () => _ = RunHelperSetupAsync(ElevatedTask.InstallHelper, form);
            form.UpdateHelperRequested += () => _ = RunHelperSetupAsync(ElevatedTask.UpdateHelper, form);
            form.RemoveHelperRequested += () => _ = RunHelperSetupAsync(ElevatedTask.UninstallHelper, form);
            if (tab is not null)
            {
                form.SelectTab(tab);
            }

            if (installHelper)
            {
                form.Shown += (_, _) => _ = RunHelperSetupAsync(ElevatedTask.InstallHelper, form);
            }

            _openSettings = form;
            if (form.ShowDialog() == DialogResult.OK)
            {
                AppSettings updated = form.Result;

                // The Alerts tab can only remove apps. An app allowed while Settings was open (from a popup or Activity)
                // must not be dropped by the list the form copied when it opened.
                var keptByForm = updated.AllowedApps.Select(a => a.AppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                updated.AllowedApps = _settings.AllowedApps
                    .Where(a => keptByForm.Contains(a.AppKey) || !allowedAtOpen.Contains(a.AppKey))
                    .Select(a => new AllowedApp { AppKey = a.AppKey, DisplayName = a.DisplayName })
                    .ToList();
                updated.Normalize();
                if (!lockedAtOpen && _status.State != LockState.Unlocked)
                {
                    // The lock was turned on (tray click) while Settings was open. Exclusions can't change
                    // during a lock, so keep the ones in force.
                    updated.ExcludedDevices = SettingsStore.Clone(_settings).ExcludedDevices;
                    _popups.Show(new AlertModel(
                        "Device exclusions not changed",
                        "The lock was turned on while Settings was open, so device exclusions were left as they were. Unlock, then change them.",
                        AlertSeverity.Info));
                }

                bool historyTurnedOff = _settings.HistoryEnabled && !updated.HistoryEnabled;
                UpdateSettings(updated);
                SetAutostart(form.StartWithWindows);
                if (historyTurnedOff && Dialogs.AskDeleteExistingHistory())
                {
                    ClearHistory();
                }

                PruneHistory();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings failed", ex);
        }
        finally
        {
            _openSettings = null;
            _settingsOpen = false;
            _helper.SetFastPolling(_status.State != LockState.Unlocked);
            RegisterHotkey(showErrors: true); // Settles on the saved hotkey (the box may have tested another one).
        }
    }

    /// <summary>Installs, updates or removes the helper, then brings everything up to date. A helper installed
    /// while locked locks the cameras right away.</summary>
    private async Task RunHelperSetupAsync(ElevatedTask task, IWin32Window? owner)
    {
        if (_helperChanging)
        {
            return;
        }

        // A newer helper is only replaced by an older one when the user says so (Settings has asked already).
        if (task == ElevatedTask.UpdateHelper && owner is not SettingsForm && HelperLink.CompareToApp(_helper.State.HelperVersion) > 0
            && !Dialogs.ConfirmReplaceNewerHelper(_helper.State.HelperVersion, AppVersion.Text, owner))
        {
            return;
        }

        _helperChanging = true;
        try
        {
            string? problem = await _helper.RunSetupAsync(task, owner);

            // Settings shows the result itself, but only if the window that started this is still open.
            bool settingsShowsIt = owner is not null && ReferenceEquals(owner, _openSettings);
            if (problem is not null && !settingsShowsIt)
            {
                _popups.Show(new AlertModel(
                    "Camera helper",
                    problem,
                    AlertSeverity.Warning,
                    Sticky: true,
                    Actions: [new AlertAction("Settings", () => ShowSettings("Camera"))],
                    GroupKey: "helper-setup"));
            }

            if (problem is null && task == ElevatedTask.InstallHelper && !_settings.LockCameras)
            {
                // Installing the helper means "lock my cameras", even if Settings is then cancelled.
                AppSettings updated = SettingsStore.Clone(_settings);
                updated.LockCameras = true;
                UpdateSettings(updated);
            }

            // A lock made while the setup ran (or its permission prompt was open) skipped the helper; and a helper that
            // was just installed or repaired has no lock yet. Either way, while the microphones are locked, the helper's
            // part follows now. A lock or unlock that started meanwhile finishes first.
            if (task != ElevatedTask.UninstallHelper || _helper.IsInstalled)
            {
                await WaitUntilIdleAsync();
                if (_micStatus.IsLockedOrPartly && WantsHelper(_settings) && _helper.CanLock && (!_helper.HasLock || !_helper.HeldOwnLock || _helperNarrowedElsewhere)
                    && _engine is LockEngine engine && !_busy)
                {
                    _busy = true;
                    try
                    {
                        string? error = await LockHelperAsync(engine, task == ElevatedTask.InstallHelper ? "camera helper installed" : "camera helper updated", afterSetup: true);
                        RecomputeStatus();
                        if (error is not null)
                        {
                            ShowHelperLockFailed(error);
                        }
                        else
                        {
                            _osd.Show(_status.State, LockedText(_status), cameras: CamerasLocked(_status));
                            RecordLockEvent(LockEventAction.Update, LockEventSource.Tray, CoveredText(_status) + " (the camera helper joined the lock)", _status.State == LockState.PartiallyLocked);
                        }
                    }
                    finally
                    {
                        _busy = false;
                    }
                }
            }

            if (task == ElevatedTask.UninstallHelper)
            {
                if (!_helper.HeldOwnLock)
                {
                    _helperHadLock = false; // The user removed it: when its service entry goes later, that is no alarm.
                }

                await RefreshLocalCamerasAsync();
            }

            RecomputeStatus();
            _openSettings?.SetHelperState(_helper.State, _status.Devices);
        }
        finally
        {
            _helperChanging = false;
        }
    }

    private void ClearHistory()
    {
        _history.Clear();
        try
        {
            _lockEvents.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Clearing the lock timeline failed", ex);
        }

        _historyCache = [];
        _lockEventCache = [];
        RefreshActivityWindow(reloadHistory: false);
    }

    /// <summary>Checks that a hotkey can be registered, then releases it again until the dialog closes.</summary>
    private string? TestHotkey(Hotkey hotkey)
    {
        HotkeyProblem problem = hotkey.Validate();
        if (problem != HotkeyProblem.None)
        {
            return HotkeyText.Describe(problem);
        }

        // Registers it for real (the old one is released); closing Settings settles on the saved hotkey again.
        return _hotkey.TryRegister(hotkey, out string? error) ? null : error;
    }

    private void RegisterHotkey(bool showErrors)
    {
        Hotkey hotkey = _settings.GetHotkey();
        bool ok = _hotkey.TryRegister(hotkey, out string? error);
        if (ok && !_hotkey.IsRegistered)
        {
            ok = false;
            error = $"{hotkey} could not be registered with Windows.";
        }

        if (ok || !showErrors)
        {
            _tray.SetStatus(_status, hotkey, CameraLockOn);
            return;
        }

        Log.Warn($"Hotkey {hotkey} unavailable: {error}");
        _popups.Show(new AlertModel(
            "Hotkey not available",
            $"{error} You can still lock and unlock by clicking the BlindsDown tray icon, or pick another hotkey in Settings.",
            AlertSeverity.Warning,
            Sticky: true,
            Actions: [new AlertAction("Settings", () => ShowSettings())]));
    }

    private void UpdateSettings(AppSettings updated)
    {
        _settings = updated;
        try
        {
            _settingsStore.Save(updated);
        }
        catch (IOException ex)
        {
            Log.Error("Saving settings failed", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Saving settings failed", ex);
        }

        _osd.HideFromScreenCapture = updated.HideOverlayFromScreenCapture;
        _popups.HideFromScreenCapture = updated.HideOverlayFromScreenCapture;
        RecomputeStatus();
        _engineThread.Post(() => _engine?.RefreshStatus());
    }

    private void RunFirstTimeSetup()
    {
        WelcomeResult welcome = Dialogs.ShowWelcome(_settings.GetHotkey().ToString(), startWithWindowsDefault: true, offerCameraLock: !_helper.IsInstalled);
        SetAutostart(welcome.StartWithWindows);
        AppSettings updated = SettingsStore.Clone(_settings);
        updated.FirstRunDone = true;
        updated.CameraOfferShown = true;
        if (welcome.LockCameras)
        {
            updated.LockCameras = true;
        }

        UpdateSettings(updated);

        // Seed the history with what Windows remembers (marked approximate).
        if (updated.HistoryEnabled && _consent is ConsentStoreMonitor consent)
        {
            DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-updated.HistoryRetentionDays);
            _engineThread.Post(() =>
            {
                try
                {
                    foreach (UsageRecord record in consent.GetLastUsedRecords(since))
                    {
                        _history.Append(record);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("History backfill failed", ex);
                }
            });
        }

        if (welcome.LockCameras && !_helper.IsInstalled)
        {
            // Settings owns the permission prompt (so it can't end up hidden behind other windows) and shows the progress.
            ShowSettings("Camera", installHelper: true);
        }
    }

    /// <summary>Someone who used BlindsDown before cameras could be locked hears about it once.</summary>
    private void OfferCameraLockOnce()
    {
        if (_settings.CameraOfferShown)
        {
            return;
        }

        AppSettings updated = SettingsStore.Clone(_settings);
        updated.CameraOfferShown = true;
        UpdateSettings(updated);
        if (_helper.IsInstalled)
        {
            return;
        }

        _popups.Show(new AlertModel(
            "New: lock your cameras too",
            "BlindsDown can now turn your cameras off while locked. It needs your permission once to install a small helper.",
            AlertSeverity.Info,
            Sticky: true,
            Actions: [new AlertAction("Set up", () => ShowSettings("Camera"))],
            GroupKey: "camera-offer"));
    }

    private static bool SafeAutostartEnabled()
    {
        try
        {
            return Autostart.IsEnabled();
        }
        catch (Exception ex)
        {
            Log.Error("Reading autostart failed", ex);
            return false;
        }
    }

    private static bool SafeAutostartDisabledInTaskManager()
    {
        try
        {
            return Autostart.IsDisabledInTaskManager();
        }
        catch (Exception ex)
        {
            Log.Error("Reading the Task Manager startup state failed", ex);
            return false;
        }
    }

    /// <summary>Deletes history and lock timeline entries older than the retention period. Safe to call from any thread.</summary>
    private void PruneHistory()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.HistoryRetentionDays);
        try
        {
            _history.Prune(cutoff);
            _lockEvents.Prune(cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Pruning history failed", ex);
        }
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            Autostart.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            Log.Error("Changing autostart failed", ex);
        }
    }

    /// <summary>A command from another program (--lock, --unlock, --toggle, --restore, or a second start). Lock and
    /// unlock run one at a time in the order they came, after startup (helper check, crash recovery) and after a
    /// running lock or unlock, and are never dropped silently.</summary>
    private void HandleCommand(InstanceCommand command)
    {
        if (command == InstanceCommand.Show)
        {
            ShowActivity();
            return;
        }

        _commandsReceived++;
        _pendingCommands.Enqueue(command);
        if (_startupDone)
        {
            _ = RunPendingCommandsAsync();
        }
        else
        {
            Log.Info($"Command {command} from another program waits until startup is finished.");
        }
    }

    private async Task RunCommandAsync(InstanceCommand command)
    {
        _waitingCommand = command;
        try
        {
            await WaitUntilIdleAsync();
            while (_exitPrompting)
            {
                // The exit question is open: wait for the answer instead of turning the request down (ExitAsync keeps
                // BlindsDown running for it).
                await Task.Delay(200);
            }

            if (_exiting)
            {
                Log.Warn($"Command {command} from another program arrived while BlindsDown was exiting; not carried out.");
                return;
            }

            await WaitUntilIdleAsync();
            if (command == InstanceCommand.Unlock && _status.State == LockState.Unlocked && _helper.IsInstalled && !_busy)
            {
                await _helper.RefreshAsync(); // It may hold a lock this app hasn't heard of yet.
                RecomputeStatus();
                while (_exitPrompting)
                {
                    await Task.Delay(200);
                }

                if (_exiting)
                {
                    Log.Warn($"Command {command} from another program arrived while BlindsDown was exiting; not carried out.");
                    return;
                }
            }
        }
        finally
        {
            _waitingCommand = null;
        }

        if (!_engineReady || _busy || _recovering || _exitPrompting)
        {
            Log.Warn($"Command {command} from another program could not run (busy or not ready).");
            _popups.Show(new AlertModel(
                "Request not carried out",
                $"Another program asked BlindsDown to {CommandWord(command)}, but BlindsDown {(_engineReady ? "was still busy" : "can't reach your microphones right now")}. Try again in a moment.",
                AlertSeverity.Warning,
                Sticky: true,
                GroupKey: "command-not-run"));
            return;
        }

        switch (command)
        {
            case InstanceCommand.Toggle:
                await ToggleAsync(LockEventSource.OtherProgram);
                break;

            // "Only cameras locked" counts as unlocked for the microphones: lock them (the helper keeps its lock).
            case InstanceCommand.Lock when _micStatus.State == LockState.Unlocked:
                await LockAsync(LockEventSource.OtherProgram);
                break;
            case InstanceCommand.Unlock:
                if (_status.State != LockState.Unlocked)
                {
                    await UnlockAsync(LockEventSource.OtherProgram);
                }
                else
                {
                    _popups.Show(new AlertModel(
                        "Nothing to unlock",
                        _helper.IsInstalled && !_helper.CanLock
                            ? "Another program asked BlindsDown to unlock. Your microphones aren't locked, but the camera helper isn't answering, so BlindsDown can't check your cameras. Try again in a moment."
                            : "Another program asked BlindsDown to unlock, but nothing is locked.",
                        AlertSeverity.Info,
                        GroupKey: "command-not-run"));
                }

                break;
            default:
                Log.Info($"Command {command} from another program: nothing to do.");
                break;
        }
    }

    /// <summary>Commands from other programs that wait for their turn and would change the lock: an unlock while anything
    /// is locked, a lock while the microphones aren't, any toggle. (One whose lock or unlock is already running isn't
    /// waiting.)</summary>
    /// <param name="all">Every waiting command: while a lock or unlock is still running, what one will do once it has
    /// finished can't be judged from the state half-way through.</param>
    private List<InstanceCommand> WaitingRequests(bool all = false) =>
    [
        .. _pendingCommands.Concat(_waitingCommand is InstanceCommand waiting ? [waiting] : []).Where(command => all || command switch
        {
            InstanceCommand.Toggle => true,
            InstanceCommand.Lock => _micStatus.State == LockState.Unlocked,
            InstanceCommand.Unlock => _status.State != LockState.Unlocked,
            _ => false,
        }),
    ];

    private static string CommandWords(IReadOnlyList<InstanceCommand> commands)
    {
        bool locks = commands.Any(c => c == InstanceCommand.Lock);
        bool unlocks = commands.Any(c => c == InstanceCommand.Unlock);
        return locks && !unlocks && commands.All(c => c == InstanceCommand.Lock) ? "lock"
            : unlocks && commands.All(c => c == InstanceCommand.Unlock) ? "unlock"
            : "lock or unlock";
    }

    private static string CommandWord(InstanceCommand command) => command switch
    {
        InstanceCommand.Lock => "lock",
        InstanceCommand.Unlock => "unlock",
        _ => "lock or unlock",
    };

    // ---------------------------------------------------------------- automatic lock

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            OnUi(() => AutoLock(_settings.AutoLockOnWindowsLock, "Windows was locked"));
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            OnUi(() => AutoLock(_settings.AutoLockOnSleep, "the PC is going to sleep"));
        }
    }

    /// <summary>Locks the microphones (and, with the helper, cameras) automatically. When a lock or unlock is running,
    /// or BlindsDown is still starting, it waits and locks right after. "Only cameras locked" still locks the microphones.</summary>
    private void AutoLock(bool wanted, string why)
    {
        if (!wanted || _exiting)
        {
            return;
        }

        if (!_engineReady || _busy || _recovering || _exitPrompting || !_startupDone)
        {
            Log.Info($"Automatic lock ({why}) waits until the current operation finishes.");
            _pendingAutoLock = why;
            return;
        }

        _pendingAutoLock = null;
        if (_micStatus.State == LockState.Unlocked)
        {
            Log.Info("Locking automatically: " + why);
            _ = LockAsync(LockEventSource.Automatic);
        }
    }

    /// <summary>Runs an automatic lock that had to wait. Posted, so an Exit that is finishing can still set _exiting first.</summary>
    private void RunPendingAutoLock()
    {
        if (_pendingAutoLock is string why)
        {
            _pendingAutoLock = null;
            BeginOnUi(() => AutoLock(wanted: true, why));
        }
    }

    // ---------------------------------------------------------------- shutdown

    private async Task ExitAsync()
    {
        if (_exiting || _exitPrompting || _recovering)
        {
            return;
        }

        if (_helperChanging)
        {
            // Its permission prompt or setup run is open: exiting now would leave the result unseen.
            _popups.Show(new AlertModel(
                "BlindsDown kept running",
                "The camera helper is being installed, updated or removed. Exit again when that is done.",
                AlertSeverity.Info,
                GroupKey: "exit-busy"));
            return;
        }

        _exitPrompting = true;
        _exitUnlockReport = null;
        _exitUnlockRecord = null;
        bool exit = false;
        try
        {
            exit = await PrepareExitAsync();
            if (exit && (!_engineReady || !_startupDone) && WaitingRequests(all: true) is { Count: > 0 } stuck
                && !Dialogs.ConfirmExitDroppingRequests(CommandWords(stuck)))
            {
                // Windows audio hasn't answered since the start, so they can't run yet: the user decides, knowing that.
                exit = false;
            }

            List<InstanceCommand> waiting = WaitingRequests();
            if (exit && waiting.Count > 0 && _engineReady && _startupDone && !_busy)
            {
                // Another program asked to lock or unlock just as the user chose to exit: BlindsDown does that and keeps
                // running, so the request isn't lost (its sender was told it was delivered) and an unlock's alarm stays.
                exit = false;
                Log.Info($"Exit cancelled: another program asked to {CommandWords(waiting)} just as the user chose to exit.");
                await OnEngineWithin(() => _engine?.ClearCleanExit(), "keeping the lock record after a cancelled exit"); // "Exit and stay locked" didn't happen.
                if (_exitUnlockReport is RestoreReport report)
                {
                    Dialogs.ShowRestoreReport(report, _popups);
                }

                _popups.Show(new AlertModel(
                    "BlindsDown kept running",
                    $"Another program asked BlindsDown to {CommandWords(waiting)} just as you chose to exit, so BlindsDown does that and keeps running. Exit again if you still want to.",
                    AlertSeverity.Warning,
                    Sticky: true,
                    GroupKey: "exit-busy"));
            }
        }
        finally
        {
            if (_exitUnlockRecord is { } record)
            {
                // "Unlock and exit": an exit unlock if BlindsDown exits, else an unlock from the tray menu.
                RecordLockEvent(LockEventAction.Unlock, exit ? LockEventSource.Exit : LockEventSource.Tray, record.Summary, record.Problems);
            }

            _exitUnlockRecord = null;
            _exitUnlockReport = null;
            _exitPrompting = false;
            if (!exit)
            {
                RunPendingAutoLock(); // BlindsDown keeps running: an automatic lock that waited runs now.
            }
        }

        if (!exit)
        {
            return;
        }

        _exiting = true;
        _pendingAutoLock = null;
        foreach (InstanceCommand command in _pendingCommands.Concat(_waitingCommand is InstanceCommand held ? [held] : []))
        {
            // Only when nothing can run it now (still busy after "Exit anyway", or Windows audio never answered).
            Log.Warn($"Command {command} from another program was not carried out: BlindsDown exited.");
        }

        foreach (InstanceCommand late in _instance.StopListening())
        {
            Log.Warn($"Command {late} from another program arrived as BlindsDown was exiting; not carried out.");
        }

        try
        {
            await _engineThread.InvokeAsync(StopEngine).WaitAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown did not complete cleanly", ex);
        }

        ExitThread();
    }

    /// <summary>Asks what to do with a lock and does it. Returns false when BlindsDown must keep running.</summary>
    private async Task<bool> PrepareExitAsync()
    {
        // A running lock or unlock counts as locked: exiting in the middle of one would leave half of it done.
        if (_engineReady && (_busy || _status.State != LockState.Unlocked))
        {
            bool? unlockFirst = Dialogs.AskExitWhileLocked(micsLocked: _micStatus.State != LockState.Unlocked, camerasLocked: _helper.HasLock);
            if (unlockFirst is null)
            {
                return false;
            }

            bool lockedWhenAsked = _busy || _status.State != LockState.Unlocked;

            // A lock or unlock may still be running (e.g. the user pressed the hotkey just before Exit).
            await WaitUntilIdleAsync();
            if (_busy)
            {
                // Stuck on Windows audio or the camera helper: never exit silently in the middle, but don't trap the user
                // either. Exiting anyway leaves the record unmarked, so the next start offers to finish or undo it.
                Log.Warn("Exit: a lock or unlock is still running.");
                bool requestWaiting = WaitingRequests(all: true).Count > 0;
                int received = _commandsReceived;
                if (!Dialogs.ConfirmExitWhileBusy(unlocking: _transition == LockState.Unlocking, micsInvolved: _micStatus.State != LockState.Unlocked, requestWaiting))
                {
                    return false;
                }

                if (!requestWaiting && _commandsReceived != received && WaitingRequests(all: true).Count > 0)
                {
                    // A request came in while the question was open, which didn't mention it: don't drop it unasked.
                    _popups.Show(new AlertModel(
                        "BlindsDown kept running",
                        "Another program asked BlindsDown to lock or unlock while you were deciding, so BlindsDown didn't exit. It does that as soon as the current step finishes. Exit again if you still want to.",
                        AlertSeverity.Warning,
                        Sticky: true,
                        GroupKey: "exit-busy"));
                    return false;
                }

                Log.Warn("Exit anyway, in the middle of a lock or unlock (the next start offers recovery).");
                return true;
            }

            if (unlockFirst == true && _status.State != LockState.Unlocked)
            {
                (RestoreReport? report, string? error) = await UnlockAsync(LockEventSource.Exit);
                _exitUnlockReport = report; // Shown if the exit is cancelled after all.

                // Closing now would hide what couldn't be restored, and a microphone that was unplugged
                // is only unmuted while BlindsDown runs. Say so, and let the user keep it running.
                if ((error is not null || report is { AllExact: false })
                    && !Dialogs.ConfirmExitAfterImperfectRestore(report, error, stillLocked: _status.State != LockState.Unlocked))
                {
                    if (report is not null)
                    {
                        Dialogs.ShowRestoreReport(report, _popups);
                    }

                    return false;
                }

                // Something locked again while the question was open (an automatic lock, another program): exiting now
                // would leave it locked or half done, which is not what "Unlock and exit" means.
                if (_busy || _micStatus.State != LockState.Unlocked)
                {
                    _popups.Show(new AlertModel(
                        "BlindsDown kept running",
                        "Your devices were locked again while the exit question was open, so BlindsDown didn't exit. Unlock and exit again, or choose to exit and stay locked.",
                        AlertSeverity.Warning,
                        Sticky: true,
                        GroupKey: "exit-busy"));
                    return false;
                }
            }
            else if (unlockFirst == false && lockedWhenAsked && _status.State == LockState.Unlocked)
            {
                // Unlocked while the question was open (another program's request finished meanwhile): exiting now would
                // close its alarm straight away, and "stay locked" isn't possible any more.
                _popups.Show(new AlertModel(
                    "BlindsDown kept running",
                    "Your devices were unlocked while the exit question was open, so BlindsDown didn't exit. Exit again if you still want to.",
                    AlertSeverity.Warning,
                    Sticky: true,
                    GroupKey: "exit-busy"));
                return false;
            }
            else if (unlockFirst == false && _status.State != LockState.Unlocked)
            {
                // The helper keeps its part of the lock by itself; the microphones are picked up at the next start. If the
                // engine doesn't answer, exiting without the mark only means the next start asks what to do.
                await OnEngineWithin(() => _engine?.MarkCleanExit(), "marking the lock to go on");
            }
        }

        return true;
    }

    /// <summary>Runs a short step on the engine thread, but gives up after <see cref="ShutdownTimeout"/>: Exit must never hang
    /// on a stuck Windows audio call.</summary>
    private async Task OnEngineWithin(Action step, string what)
    {
        try
        {
            await _engineThread.InvokeAsync(() =>
            {
                step();
                return true;
            }).WaitAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            Log.Warn($"Exit: {what} didn't finish: {ex.Message}");
        }
    }

    /// <summary>Windows is signing out or shutting down: record that a lock (if any) should simply continue.</summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        try
        {
            _engineThread.Invoke(
                () =>
                {
                    _engine?.MarkCleanExit();
                    _tracker?.EndAll();
                    return true;
                },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            Log.Error("Session-end bookkeeping failed", ex);
        }
    }

    /// <summary>Runs on the engine thread.</summary>
    private void StopEngine()
    {
        _tracker?.EndAll();
        _watcher?.Dispose();
        _sessions?.Dispose();
        _cameras?.Dispose();
        _consent?.Dispose();
        _backend?.Dispose();
        _watcher = null;
        _sessions = null;
        _cameras = null;
        _consent = null;
        _backend = null;
    }

    /// <summary>Like <see cref="OnUi"/>, but always queued (never run inline), so the current handler finishes first.</summary>
    private void BeginOnUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _uiMarshal.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The UI is shutting down.
        }
    }

    private void OnUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_uiMarshal.InvokeRequired)
            {
                _uiMarshal.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (InvalidOperationException)
        {
            // The UI is shutting down (this includes ObjectDisposedException).
        }
    }

    private static string DeviceWord(CaptureKind kind) => kind == CaptureKind.Camera ? "camera" : "microphone";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes < 1 ? $"{Math.Max(1, (int)duration.TotalSeconds)} s"
        : duration.TotalHours < 1 ? $"{(int)duration.TotalMinutes} min"
        : $"{(int)duration.TotalHours} h {duration.Minutes} min";

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _helper.Dispose();
            _activity?.Dispose();
            _popups.Dispose();
            _overlay.Dispose();
            _osd.Dispose();
            _hotkey.Dispose();
            _tray.Dispose();
            _sounds.Dispose();
            _engineThread.Dispose();
            _uiMarshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
