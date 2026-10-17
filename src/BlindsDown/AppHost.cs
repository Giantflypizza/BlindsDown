using BlindsDown.Audio;
using BlindsDown.Core.IO;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Time;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using BlindsDown.Usage;
using Microsoft.Win32;

namespace BlindsDown;

/// <summary>
/// Wires everything together. UI objects live on the UI thread; device and usage logic lives on the
/// <see cref="EngineThread"/>. Events from the engine are marshalled back to the UI with <see cref="OnUi"/>.
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    /// <summary>Who asked for a lock/unlock (logged; outside requests get extra-visible feedback).</summary>
    private enum ToggleSource
    {
        /// <summary>Hotkey or tray icon.</summary>
        User,

        /// <summary>A forwarded command line (--toggle/--lock/--unlock/--restore) or another program.</summary>
        OtherProgram,

        /// <summary>The crash-recovery dialog at startup.</summary>
        Recovery,

        /// <summary>"Unlock and exit".</summary>
        Exit,
    }

    private static readonly TimeSpan UsageAlertRepeat = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OperationPatience = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HistoryPruneInterval = TimeSpan.FromHours(6);

    /// <summary>Browsers capture for many websites; Windows only tells us the browser.</summary>
    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe",
    };

    private readonly AppPaths _paths;
    private readonly SingleInstance _instance;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _history;
    private readonly EngineThread _engineThread;
    private readonly ProcessIdentityResolver _identities = new();
    private readonly Control _uiMarshal = new();
    private readonly TrayController _tray = new();
    private readonly HotkeyManager _hotkey = new();
    private readonly LockOverlayManager _overlay = new();
    private readonly OsdFlash _osd = new();
    private readonly SoundService _sounds = new();
    private readonly AlertPopupManager _popups = new();
    private readonly Dictionary<string, DateTimeOffset> _lastUsageAlert = new(StringComparer.OrdinalIgnoreCase);

    // Owned by the engine thread.
    private CoreAudioMicBackend? _backend;
    private LockEngine? _engine;
    private AudioDeviceWatcher? _watcher;
    private MicSessionMonitor? _sessions;
    private ConsentStoreMonitor? _consent;
    private UsageTracker? _tracker;

    // UI thread state. Settings are replaced as a whole on save, never mutated in place, so the engine
    // thread can safely read the current instance.
    private volatile AppSettings _settings;
    private LockStatusInfo _status = LockStatusInfo.Unlocked;
    private IReadOnlyList<UsageSession> _activeUsage = [];
    private IReadOnlyList<UsageRecord> _historyCache = [];
    private ActivityForm? _activity;
    private bool _engineReady;
    private bool _busy;
    private bool _settingsOpen;
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
        _engineThread = new EngineThread("BlindsDown devices", ex => Log.Error("Device thread error", ex));

        NetworkTripwire.Install(name => OnUi(() =>
        {
            Log.Error("Networking assembly loaded: " + name);
            _popups.Show(new AlertModel(
                "Unexpected networking code loaded",
                $"The component \"{name}\" was loaded into BlindsDown. BlindsDown never uses the network, so this should not happen. Please report it.",
                AlertSeverity.Danger,
                Sticky: true));
        }));

        _tray.ToggleRequested += () => _ = ToggleAsync(ToggleSource.User);
        _tray.ActivityRequested += ShowActivity;
        _tray.SettingsRequested += ShowSettings;
        _tray.AboutRequested += () => Dialogs.ShowAbout(AppVersion.Text);
        _tray.ExitRequested += () => _ = ExitAsync();
        _hotkey.Pressed += () => _ = ToggleAsync(ToggleSource.User);
        _instance.CommandReceived += command => OnUi(() => HandleCommand(command));
        _instance.Listen();
        SystemEvents.SessionEnding += OnSessionEnding;

        _osd.HideFromScreenCapture = _settings.HideOverlayFromScreenCapture;
        _popups.HideFromScreenCapture = _settings.HideOverlayFromScreenCapture;
        _tray.Show();
        _tray.SetStatus(_status, _settings.GetHotkey());
        _ = StartAsync(startupCommand, autostarted);
    }

    private async Task StartAsync(InstanceCommand startupCommand, bool autostarted)
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
            return;
        }

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

        if (SystemInfo.IsRemoteSession() && !autostarted)
        {
            _popups.Show(new AlertModel(
                "Remote Desktop session",
                "In a Remote Desktop session the microphones you see belong to the remote connection. The lock applies to those.",
                AlertSeverity.Info));
        }

        if (startupCommand != InstanceCommand.Show)
        {
            HandleCommand(startupCommand);
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
        engine.StatusChanged += status => OnUi(() => ApplyStatus(status));
        engine.AlertRaised += alert => OnUi(() => ShowEngineAlert(alert));

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

        var consent = new ConsentStoreMonitor(_identities);
        consent.Changed += () => _engineThread.Post(ReportUsageRegistry);

        _backend = backend;
        _engine = engine;
        _tracker = tracker;
        _watcher = watcher;
        _sessions = sessions;
        _consent = consent;

        StartupRecovery? recovery = engine.Initialize();
        if (engine.JournalWasCorrupt)
        {
            OnUi(() => _popups.Show(new AlertModel(
                "Lock record damaged",
                "BlindsDown's record of the last lock could not be read, so it can't tell which microphones it muted. Please check in Windows Sound settings that your microphones are not muted.",
                AlertSeverity.Warning,
                Sticky: true,
                Actions: [new AlertAction("Open Sound settings", SafeShell.OpenSoundSettings)])));
        }

        TryStart(watcher.Start, "device watcher");
        TryStart(sessions.Start, "audio session monitor");
        TryStart(consent.Start, "usage registry monitor");

        _engineThread.Every(TimeSpan.FromSeconds(5), engine.Reconcile);
        _engineThread.Every(TimeSpan.FromSeconds(2), ReportMicSessions);
        _engineThread.Every(TimeSpan.FromSeconds(1), tracker.Tick);
        _engineThread.Every(TimeSpan.FromSeconds(10), ReportUsageRegistry);
        _engineThread.Every(HistoryPruneInterval, PruneHistory);
        ReportMicSessions();
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

    private Task ToggleAsync(ToggleSource source)
    {
        if (!_engineReady || _busy)
        {
            return Task.CompletedTask;
        }

        return _status.State == LockState.Unlocked ? LockAsync(source) : UnlockAsync(source);
    }

    private async Task LockAsync(ToggleSource source)
    {
        if (_busy || _engine is not LockEngine engine)
        {
            return;
        }

        _busy = true;
        try
        {
            LockStatusInfo status = await WithPatienceAsync(_engineThread.InvokeAsync(engine.Lock), "locking");
            ApplyStatus(status);
            Log.Info($"Lock ({source}): {status.State}");
            if (_settings.PlaySounds)
            {
                _sounds.Play(SoundCue.Lock);
            }

            int muted = status.Devices.Count(d => d.Protection is DeviceProtection.HardwareMute or DeviceProtection.SoftwareMute);
            _osd.Show(
                status.State,
                status.State == LockState.PartiallyLocked
                    ? $"{status.Problems.Count} microphone(s) could not be locked"
                    : muted == 1 ? "1 microphone muted" : $"{muted} microphones muted");
            ReportAppsAlreadyListening(status);
        }
        catch (Exception ex)
        {
            Log.Error("Lock failed", ex);
            _popups.Show(new AlertModel("Lock failed", "BlindsDown could not lock your microphones: " + ex.Message, AlertSeverity.Danger, Sticky: true));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Unlocks and shows the restore report. Returns the report (null if there was no lock) and
    /// an error message if the unlock failed.</summary>
    private async Task<(RestoreReport? Report, string? Error)> UnlockAsync(ToggleSource source)
    {
        if (_busy || _engine is not LockEngine engine)
        {
            return (null, null);
        }

        _busy = true;
        try
        {
            RestoreReport? report = await WithPatienceAsync(_engineThread.InvokeAsync(engine.Unlock), "unlocking");
            Log.Info($"Unlock ({source}): " + (report?.Summary ?? "nothing to restore"));

            // An unlock the user didn't ask for directly (another program sent the command) must never be
            // quiet: always the sound, and a red popup that stays.
            bool fromElsewhere = source == ToggleSource.OtherProgram;
            if (_settings.PlaySounds || fromElsewhere)
            {
                _sounds.Play(SoundCue.Unlock);
            }

            _osd.Show(LockState.Unlocked);
            if (fromElsewhere && report is not null)
            {
                _popups.Show(new AlertModel(
                    "Unlocked by another program",
                    $"A program on this PC told BlindsDown to unlock (for example \"BlindsDown.exe --unlock\") at {DateTime.Now:t}. If that wasn't you, lock again and check which programs are running.",
                    AlertSeverity.Danger,
                    Sticky: true,
                    GroupKey: "unlocked-by-other"));
            }

            if (report is not null && source != ToggleSource.Exit)
            {
                Dialogs.ShowRestoreReport(report, _popups);
            }

            return (report, null);
        }
        catch (Exception ex)
        {
            Log.Error("Unlock failed", ex);
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
            _busy = false;
        }
    }

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

    /// <summary>After locking: tell the user which apps already had a microphone open (they now hear silence).
    /// Apps on the "always allow" list are left out, so a permanent router like SteelSeries Sonar isn't repeated.</summary>
    private void ReportAppsAlreadyListening(LockStatusInfo status)
    {
        var protectedIds = status.Devices
            .Where(d => d.Protection is DeviceProtection.HardwareMute or DeviceProtection.SoftwareMute)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> apps = _activeUsage
            .Where(s => s.Kind == CaptureKind.Microphone && protectedIds.Contains(s.DeviceId) && !_settings.IsAppAllowed(s.App.GroupKey))
            .Select(s => s.App.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (apps.Count > 0)
        {
            _popups.Show(new AlertModel(
                apps.Count == 1 ? $"{apps[0]} had your microphone open" : "Apps had your microphone open",
                $"{string.Join(", ", apps)} now only hear silence.",
                AlertSeverity.Info));
        }
    }

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
                $"Your microphones have been locked since {recovery.LockedSince.ToLocalTime():g}. Press {_settings.GetHotkey()} or click the tray icon to unlock.",
                AlertSeverity.Info));
            return;
        }

        Log.Warn($"Recovering from an unclean exit while {recovery.Phase}");
        if (Dialogs.AskRecovery(recovery) == RecoveryChoice.Restore)
        {
            await UnlockAsync(ToggleSource.Recovery);
        }
        else if (_engine is LockEngine engine)
        {
            LockStatusInfo status = await _engineThread.InvokeAsync(engine.Lock); // Finishes a half-done lock.
            ApplyStatus(status);
        }
    }

    private void ApplyStatus(LockStatusInfo status)
    {
        _status = status;
        _tray.SetStatus(status, _settings.GetHotkey());
        _overlay.Update(status.State, _settings);
    }

    private void ShowEngineAlert(EngineAlert alert)
    {
        Log.Info($"Alert {alert.Kind}: {alert.Message}");
        (string title, AlertSeverity severity, bool sticky) = alert.Kind switch
        {
            EngineAlertKind.UnmutedWhileLocked => ("Microphone unmuted while locked", AlertSeverity.Danger, true),
            EngineAlertKind.Tampering => ("Something keeps unmuting a microphone", AlertSeverity.Danger, true),
            EngineAlertKind.LockFailed => ("A microphone could not be locked", AlertSeverity.Danger, true),
            EngineAlertKind.NewDeviceLocked => ("New microphone locked", AlertSeverity.Warning, true),
            EngineAlertKind.DeviceRelocked => ("Microphone locked again", AlertSeverity.Warning, true),
            EngineAlertKind.DeferredRestoreDone => ("Microphone restored", AlertSeverity.Success, false),
            _ => ("BlindsDown", AlertSeverity.Info, false),
        };

        bool isNew = _popups.Show(new AlertModel(title, alert.Message, severity, sticky, GroupKey: $"engine|{alert.Kind}|{alert.DeviceName}"));
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
        // While the microphones are locked, every app that opens one is shown, even apps on the "always allow"
        // list and even with popups switched off: that is exactly when the user wants to know.
        bool micLocked = session.Kind == CaptureKind.Microphone && _status.IsLockedOrPartly;
        if (!micLocked && (!_settings.AlertsEnabled || _settings.IsAppAllowed(session.App.GroupKey)))
        {
            return;
        }

        string key = session.Kind + "|" + session.App.GroupKey;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!micLocked && _lastUsageAlert.TryGetValue(key, out DateTimeOffset last) && now - last < UsageAlertRepeat)
        {
            return;
        }

        _lastUsageAlert[key] = now;
        string app = session.App.DisplayName;
        string device = session.ViaApp is null ? session.DeviceName : $"{session.DeviceName}, a virtual microphone from {session.ViaApp}";
        string browserNote = BrowserExes.Contains(Path.GetFileName(session.App.ExePath) ?? string.Empty)
            ? " Windows doesn't say which website."
            : string.Empty;
        AlertAction allow = new("Always allow", () => AllowApp(session.App));
        AlertAction details = new("Activity", ShowActivity);

        if (!micLocked)
        {
            _popups.Show(new AlertModel(
                $"{app} is using your {DeviceWord(session.Kind)}",
                device + "." + browserNote,
                AlertSeverity.Info,
                Actions: [allow, details],
                GroupKey: "usage|" + key));
            return;
        }

        // Locked: say exactly what this microphone's protection is. Never claim "silence" for a mic that
        // is excluded, failed to lock, or isn't known.
        DeviceLockInfo? mic = _status.Devices.FirstOrDefault(d => string.Equals(d.Id, session.DeviceId, StringComparison.OrdinalIgnoreCase));
        string groupKey = $"usage-locked|{session.App.GroupKey}|{session.DeviceId}";
        AlertModel model = mic?.Protection switch
        {
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

    private void AllowApp(AppIdentity app)
    {
        AppSettings updated = SettingsStore.Clone(_settings);
        if (!updated.IsAppAllowed(app.GroupKey))
        {
            updated.AllowedApps.Add(new AllowedApp { AppKey = app.GroupKey, DisplayName = app.DisplayName });
        }

        UpdateSettings(updated);
        _popups.Show(new AlertModel(
            $"No more popups for {app.DisplayName}",
            "Its microphone and camera use still appears in Activity. You can undo this in Settings > Alerts.",
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
            try
            {
                DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.HistoryRetentionDays);
                _historyCache = _history.ReadAll().Where(r => r.End >= cutoff).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Reading history failed", ex);
            }
        }

        _activity.SetData(_activeUsage, _historyCache);
    }

    private void ShowSettings()
    {
        if (_settingsOpen)
        {
            return;
        }

        _settingsOpen = true;
        _hotkey.Suspend(); // So pressing the current hotkey in the hotkey box doesn't toggle the lock.
        bool lockedAtOpen = _status.State != LockState.Unlocked;
        try
        {
            using var form = new SettingsForm(
                SettingsStore.Clone(_settings),
                _status.Devices,
                locked: _status.State != LockState.Unlocked,
                startWithWindows: SafeAutostartEnabled(),
                tryHotkey: TestHotkey,
                startupDisabledInTaskManager: SafeAutostartDisabledInTaskManager());
            form.ClearHistoryRequested += () =>
            {
                _history.Clear();
                _historyCache = [];
                RefreshActivityWindow(reloadHistory: false);
            };

            if (form.ShowDialog() == DialogResult.OK)
            {
                AppSettings updated = form.Result;
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
                    _history.Clear();
                    _historyCache = [];
                    RefreshActivityWindow(reloadHistory: false);
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
            _settingsOpen = false;
            _hotkey.Resume(); // Registers whatever was last registered or tested; RegisterHotkey then settles on the saved one.
            RegisterHotkey(showErrors: true);
        }
    }

    /// <summary>Checks that a hotkey can be registered, then releases it again until the dialog closes.</summary>
    private string? TestHotkey(Hotkey hotkey)
    {
        HotkeyProblem problem = hotkey.Validate();
        if (problem != HotkeyProblem.None)
        {
            return HotkeyText.Describe(problem);
        }

        // While Settings is open the hotkey manager is suspended, so this only proves the combination is free.
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
            _tray.SetStatus(_status, hotkey);
            return;
        }

        Log.Warn($"Hotkey {hotkey} unavailable: {error}");
        _popups.Show(new AlertModel(
            "Hotkey not available",
            $"{error} You can still lock and unlock by clicking the BlindsDown tray icon, or pick another hotkey in Settings.",
            AlertSeverity.Warning,
            Sticky: true,
            Actions: [new AlertAction("Settings", ShowSettings)]));
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
        _overlay.Update(_status.State, updated);
        _tray.SetStatus(_status, updated.GetHotkey());
        _engineThread.Post(() => _engine?.RefreshStatus());
    }

    private void RunFirstTimeSetup()
    {
        bool startWithWindows = Dialogs.ShowWelcome(_settings.GetHotkey().ToString(), startWithWindowsDefault: true);
        SetAutostart(startWithWindows);
        AppSettings updated = SettingsStore.Clone(_settings);
        updated.FirstRunDone = true;
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

    /// <summary>Deletes history older than the retention period. Safe to call from any thread.</summary>
    private void PruneHistory()
    {
        try
        {
            _history.Prune(DateTimeOffset.UtcNow.AddDays(-_settings.HistoryRetentionDays));
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

    private void HandleCommand(InstanceCommand command)
    {
        switch (command)
        {
            case InstanceCommand.Toggle:
                _ = ToggleAsync(ToggleSource.OtherProgram);
                break;
            case InstanceCommand.Lock when _status.State == LockState.Unlocked:
                _ = LockAsync(ToggleSource.OtherProgram);
                break;
            case InstanceCommand.Unlock when _status.State != LockState.Unlocked:
                _ = UnlockAsync(ToggleSource.OtherProgram);
                break;
            case InstanceCommand.Show:
                ShowActivity();
                break;
        }
    }

    // ---------------------------------------------------------------- shutdown

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_engineReady && _status.State != LockState.Unlocked)
        {
            bool? unlockFirst = Dialogs.AskExitWhileLocked();
            if (unlockFirst is null)
            {
                return;
            }

            // A lock or unlock may still be running (e.g. the user pressed the hotkey just before Exit).
            await WaitUntilIdleAsync();
            if (unlockFirst == true && _status.State != LockState.Unlocked)
            {
                (RestoreReport? report, string? error) = await UnlockAsync(ToggleSource.Exit);

                // Closing now would hide what couldn't be restored, and a microphone that was unplugged
                // is only unmuted while BlindsDown runs. Say so, and let the user keep it running.
                if ((error is not null || report is { AllExact: false })
                    && !Dialogs.ConfirmExitAfterImperfectRestore(report, error))
                {
                    if (report is not null)
                    {
                        Dialogs.ShowRestoreReport(report, _popups);
                    }

                    return;
                }
            }
            else if (unlockFirst == false && _status.State != LockState.Unlocked)
            {
                await _engineThread.InvokeAsync(() => _engine?.MarkCleanExit());
            }
        }

        _exiting = true;
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
        _consent?.Dispose();
        _backend?.Dispose();
        _watcher = null;
        _sessions = null;
        _consent = null;
        _backend = null;
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
