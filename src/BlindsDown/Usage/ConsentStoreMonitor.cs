using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;

namespace BlindsDown.Usage;

/// <summary>
/// Fallback usage source: Windows' per-app usage registry (HKCU + HKLM ConsentStore, microphone and webcam),
/// watched with RegNotifyChangeKeyValue plus a 10 s poll. Thread-safe.
/// <see cref="Changed"/> is raised on a thread-pool thread.
///
/// Entries are matched to running processes without ever decoding desktop-app key names: '#' is a legal
/// path character, so each running exe path is encoded the way Windows does and compared instead.
/// Identities come from <see cref="AppIdentityRules"/>, the same rules <see cref="ProcessIdentityResolver"/>
/// uses, so the usage tracker can tell that a registry entry and a live audio session are the same app.
/// </summary>
internal sealed class ConsentStoreMonitor : IDisposable
{
    public const string SourceName = "usage-registry";

    /// <summary>Windows writes several values per start/stop; one Changed per burst is enough.</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>A process that exits without Windows recording a stop never touches the registry,
    /// so stale "in use" entries are only noticed by polling.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly IProcessIdentityResolver _identities;
    private readonly IConsentStoreSource _source;
    private readonly Func<IReadOnlyCollection<string>> _runningExePaths;
    private readonly Func<DateTimeOffset> _bootTime;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounce;
    private System.Threading.Timer? _poll;
    private string? _lastPollState;
    private bool _started;
    private volatile bool _disposed;

    public ConsentStoreMonitor(IProcessIdentityResolver identities)
        : this(identities, new RegistryConsentStoreSource(), () => RunningProcessTable.Shared.Current().ExePaths, SystemInfo.BootTimeUtc)
    {
    }

    /// <param name="runningExePaths">Full exe paths of the running processes.</param>
    /// <param name="pollInterval">Tests shorten it; the app uses 10 s.</param>
    internal ConsentStoreMonitor(
        IProcessIdentityResolver identities,
        IConsentStoreSource source,
        Func<IReadOnlyCollection<string>> runningExePaths,
        Func<DateTimeOffset> bootTime,
        TimeSpan? pollInterval = null)
    {
        _identities = identities;
        _source = source;
        _runningExePaths = runningExePaths;
        _bootTime = bootTime;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _debounce = new System.Threading.Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _source.EnsureWatching(OnSourceChanged);
        string state = ActiveStateFingerprint();
        lock (_gate)
        {
            _lastPollState = state;
            if (!_disposed)
            {
                _poll = new System.Threading.Timer(_ => Poll(), null, _pollInterval, _pollInterval);
            }
        }
    }

    /// <summary>Apps the registry says are using the device right now (stale entries filtered out).
    /// DeviceId is empty because the registry doesn't name the device.</summary>
    public IReadOnlyList<UsageSignal> GetActiveSignals(CaptureKind kind)
    {
        try
        {
            DateTimeOffset boot = _bootTime();
            Dictionary<string, string>? runningExes = null;
            var signals = new Dictionary<string, UsageSignal>(StringComparer.OrdinalIgnoreCase);
            foreach (ConsentStoreEntry entry in _source.Read(kind))
            {
                // ConsentStore.LooksInUse(start, stop, boot, processRunning), split in two: its time checks
                // (started, not stopped, since boot) run first, so the process lookup below only runs for the
                // few candidates, not for every app that ever used the device.
                if (!ConsentStore.LooksInUse(entry.LastUsedTimeStart, entry.LastUsedTimeStop, boot, processRunning: true))
                {
                    continue;
                }

                AppIdentity? app = RunningIdentity(entry, ref runningExes);
                if (app is null)
                {
                    // Not running: a leftover from a crash or a missed stop.
                    continue;
                }

                // HKCU and HKLM can both list an app; report it once.
                signals.TryAdd(app.Key, new UsageSignal(kind, app, ProcessId: null, DeviceId: string.Empty, DeviceName: DeviceNameFor(kind)));
            }

            return [.. signals.Values];
        }
        catch (Exception ex)
        {
            Log.Error("Reading the usage registry failed", ex);
            return [];
        }
    }

    /// <summary>"Last used" entries from the registry since <paramref name="since"/>, for a first-run
    /// history backfill (records have Approximate = true), newest first.</summary>
    public IReadOnlyList<UsageRecord> GetLastUsedRecords(DateTimeOffset since)
    {
        try
        {
            var records = new List<UsageRecord>();
            var seen = new HashSet<(CaptureKind, string, long, long)>();
            Dictionary<string, string>? runningExes = null;
            foreach (CaptureKind kind in Enum.GetValues<CaptureKind>())
            {
                foreach (ConsentStoreEntry entry in _source.Read(kind))
                {
                    DateTimeOffset? start = ConsentStore.FromFileTime(entry.LastUsedTimeStart);
                    DateTimeOffset? stop = ConsentStore.FromFileTime(entry.LastUsedTimeStop);
                    if (start is null || stop is null || start < since || stop < since || stop <= start)
                    {
                        continue;
                    }

                    AppIdentity app = HistoricIdentity(entry, ref runningExes);
                    if (!seen.Add((kind, app.GroupKey, entry.LastUsedTimeStart, entry.LastUsedTimeStop)))
                    {
                        continue;
                    }

                    records.Add(new UsageRecord
                    {
                        Kind = kind,
                        AppKey = app.GroupKey,
                        AppName = app.DisplayName,
                        ExePath = app.ExePath,
                        DeviceName = DeviceNameFor(kind),
                        Start = start.Value,
                        End = stop.Value,
                        Approximate = true,
                    });
                }
            }

            records.Sort((a, b) => b.Start.CompareTo(a.Start));
            return records;
        }
        catch (Exception ex)
        {
            Log.Error("Reading usage-registry history failed", ex);
            return [];
        }
    }

    public void Dispose()
    {
        System.Threading.Timer? poll;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            poll = _poll;
            _poll = null;
        }

        poll?.Dispose();
        _debounce.Dispose();
        _source.Dispose();
    }

    internal static string DeviceNameFor(CaptureKind kind) => kind == CaptureKind.Camera ? "Camera" : "Microphone";

    /// <summary>The identity of the running process behind an entry, or null when none is running.</summary>
    private AppIdentity? RunningIdentity(ConsentStoreEntry entry, ref Dictionary<string, string>? runningExes)
    {
        if (entry.IsPackaged)
        {
            return _identities.IsPackageRunning(entry.KeyName) ? AppIdentityRules.ForPackage(entry.KeyName) : null;
        }

        runningExes ??= RunningExesByKeyName();
        return runningExes.TryGetValue(entry.KeyName, out string? exePath) ? AppIdentityRules.ForExePath(exePath) : null;
    }

    /// <summary>For history: the exact path when the app is running, else a best-effort decode (display only).</summary>
    private AppIdentity HistoricIdentity(ConsentStoreEntry entry, ref Dictionary<string, string>? runningExes)
    {
        if (entry.IsPackaged)
        {
            return AppIdentityRules.ForPackage(entry.KeyName);
        }

        runningExes ??= RunningExesByKeyName();
        return AppIdentityRules.ForExePath(
            runningExes.TryGetValue(entry.KeyName, out string? exePath) ? exePath : ConsentStore.DecodeNonPackagedKeyNameForDisplay(entry.KeyName));
    }

    /// <summary>Encoded key name -> running exe path.</summary>
    private Dictionary<string, string> RunningExesByKeyName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string exePath in _runningExePaths())
        {
            map.TryAdd(ConsentStore.EncodeNonPackagedKeyName(exePath), exePath);
        }

        return map;
    }

    private string ActiveStateFingerprint() => string.Join(
        "\n",
        Enum.GetValues<CaptureKind>()
            .SelectMany(GetActiveSignals)
            .Select(s => s.Kind + "|" + s.App.Key)
            .Order(StringComparer.OrdinalIgnoreCase));

    private void OnSourceChanged()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _source.EnsureWatching(OnSourceChanged);
            string state = ActiveStateFingerprint();
            bool changed;
            lock (_gate)
            {
                changed = _lastPollState is not null && !string.Equals(state, _lastPollState, StringComparison.Ordinal);
                _lastPollState = state;
            }

            if (changed)
            {
                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Usage-registry poll failed", ex);
        }
    }

    private void RaiseChanged()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // An exception escaping a timer callback would end the process.
            Log.Error("A usage-registry Changed handler failed", ex);
        }
    }
}
