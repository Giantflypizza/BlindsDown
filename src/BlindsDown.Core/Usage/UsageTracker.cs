using BlindsDown.Core.Time;

namespace BlindsDown.Core.Usage;

/// <summary>
/// Turns raw "app X is using device Y right now" reports from several sources into clean
/// start/stop sessions. Each source reports its complete current list every time it looks;
/// a session ends once no source has listed it for <see cref="StopGrace"/>.
///
/// Sources that cannot name the device (the usage registry) are ignored for an app while a
/// device-aware source reports that app (or did within the grace period), and a use the registry
/// saw first is taken over once a device-aware source names its device, so the same use is never
/// counted twice.
///
/// Not thread-safe: called from one thread.
/// </summary>
public sealed class UsageTracker
{
    private readonly IClock _clock;
    private readonly Dictionary<string, Dictionary<string, UsageSignal>> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tracked> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public UsageTracker(IClock clock, TimeSpan? stopGrace = null, TimeSpan? confirmAfter = null)
    {
        _clock = clock;
        StopGrace = stopGrace ?? TimeSpan.FromSeconds(3);
        ConfirmAfter = confirmAfter ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>How long a session may go unreported before it counts as ended.</summary>
    public TimeSpan StopGrace { get; }

    /// <summary>Sessions shorter than this never raise <see cref="Confirmed"/> (filters device probes).</summary>
    public TimeSpan ConfirmAfter { get; }

    /// <summary>Raised as soon as a new use is seen.</summary>
    public event Action<UsageSession>? Started;

    /// <summary>Raised once a use has lasted <see cref="ConfirmAfter"/>. Alerts use this.</summary>
    public event Action<UsageSession>? Confirmed;

    /// <summary>Raised when a use has ended (End is set). History records use this.</summary>
    public event Action<UsageSession>? Ended;

    public IReadOnlyList<UsageSession> ActiveSessions =>
        _sessions.Values.Where(t => t.MissingSince is null).Select(t => t.Session).ToList();

    /// <summary>Replaces everything <paramref name="source"/> reported before with this list.</summary>
    public void Report(string source, IEnumerable<UsageSignal> activeNow)
    {
        var map = new Dictionary<string, UsageSignal>(StringComparer.OrdinalIgnoreCase);
        foreach (UsageSignal signal in activeNow)
        {
            map[KeyOf(signal)] = signal;
        }

        _bySource[source] = map;
        Update();
    }

    /// <summary>Call about once a second: confirms and ends sessions on time.</summary>
    public void Tick() => Update();

    /// <summary>Ends every open session now (e.g. on exit), so history is complete.</summary>
    public void EndAll()
    {
        DateTimeOffset now = _clock.UtcNow;
        foreach (Tracked t in _sessions.Values.ToList())
        {
            Finish(t, t.MissingSince ?? now);
        }

        _bySource.Clear();
    }

    private void Update()
    {
        DateTimeOffset now = _clock.UtcNow;

        // Merge all sources. Device-aware reports win over device-unknown ones for the same app.
        var current = new Dictionary<string, (UsageSignal Signal, List<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        foreach ((string source, Dictionary<string, UsageSignal> signals) in _bySource)
        {
            foreach ((string key, UsageSignal signal) in signals)
            {
                if (!current.TryGetValue(key, out var entry))
                {
                    entry = (signal, []);
                    current[key] = entry;
                }

                entry.Sources.Add(source);
            }
        }

        // An app's use known to a device-aware source (reported now, or still within its grace period)
        // hides that app's device-unknown reports. The registry tends to lag behind the live sources at
        // both ends of a use, and must not turn its tail into a second use.
        var appsWithKnownDevice = new HashSet<string>(
            current.Values.Where(v => !v.Signal.DeviceUnknown).Select(v => AppKindKey(v.Signal))
                .Concat(_sessions.Values.Where(t => t.Session.DeviceId.Length > 0).Select(t => AppKindKey(t.Session))),
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in current.Where(kv => kv.Value.Signal.DeviceUnknown && appsWithKnownDevice.Contains(AppKindKey(kv.Value.Signal))).Select(kv => kv.Key).ToList())
        {
            current.Remove(key);
        }

        // New or continuing sessions.
        foreach ((string key, (UsageSignal signal, List<string> sources)) in current)
        {
            if (_sessions.TryGetValue(key, out Tracked? tracked))
            {
                tracked.MissingSince = null;
                tracked.Session = tracked.Session with { Sources = sources, ProcessId = signal.ProcessId ?? tracked.Session.ProcessId };
            }
            else if (!signal.DeviceUnknown && _sessions.Remove(DeviceUnknownKey(signal), out Tracked? unnamed))
            {
                // A device-unknown source saw this use first and a device-aware one now names the device.
                // Same use: it keeps its start time and alerts and just learns the device.
                unnamed.MissingSince = null;
                unnamed.Session = unnamed.Session with
                {
                    DeviceId = signal.DeviceId,
                    DeviceName = signal.DeviceName,
                    ViaApp = signal.ViaApp ?? unnamed.Session.ViaApp,
                    ProcessId = signal.ProcessId ?? unnamed.Session.ProcessId,
                    Sources = sources,
                };
                _sessions[key] = unnamed;
            }
            else
            {
                var session = new UsageSession
                {
                    Id = Guid.NewGuid(),
                    Kind = signal.Kind,
                    App = signal.App,
                    DeviceId = signal.DeviceId,
                    DeviceName = signal.DeviceName,
                    ProcessId = signal.ProcessId,
                    ViaApp = signal.ViaApp,
                    Start = now,
                    Sources = sources,
                };
                tracked = new Tracked(session);
                _sessions[key] = tracked;
                Started?.Invoke(session);
            }
        }

        // Missing sessions: wait for the grace period, then end them at the moment they disappeared.
        foreach ((string key, Tracked tracked) in _sessions.ToList())
        {
            if (current.ContainsKey(key) || !_sessions.TryGetValue(key, out Tracked? live) || live != tracked)
            {
                continue; // Still reported, or already ended by an Ended handler that called back into us.
            }

            tracked.MissingSince ??= now;
            if (now - tracked.MissingSince.Value >= StopGrace)
            {
                Finish(tracked, tracked.MissingSince.Value);
            }
        }

        // Confirm sessions that lasted long enough. (Collected first: handlers may call back into the tracker.)
        var toConfirm = _sessions.Values
            .Where(t => !t.ConfirmedRaised && t.MissingSince is null && now - t.Session.Start >= ConfirmAfter)
            .ToList();
        foreach (Tracked tracked in toConfirm)
        {
            if (tracked.ConfirmedRaised || !_sessions.ContainsValue(tracked))
            {
                continue; // A handler called back into the tracker, which confirmed or ended this one already.
            }

            tracked.ConfirmedRaised = true;
            Confirmed?.Invoke(tracked.Session);
        }
    }

    private void Finish(Tracked tracked, DateTimeOffset end)
    {
        _sessions.Remove(KeyOf(tracked.Session));
        Ended?.Invoke(tracked.Session with { End = end });
    }

    private static string KeyOf(UsageSignal s) => $"{s.Kind}|{s.App.Key}|{s.DeviceId}";

    private static string KeyOf(UsageSession s) => $"{s.Kind}|{s.App.Key}|{s.DeviceId}";

    private static string AppKindKey(UsageSignal s) => $"{s.Kind}|{s.App.Key}";

    private static string AppKindKey(UsageSession s) => $"{s.Kind}|{s.App.Key}";

    /// <summary>The key a device-unknown report of the same app and kind is tracked under.</summary>
    private static string DeviceUnknownKey(UsageSignal s) => $"{s.Kind}|{s.App.Key}|";

    private sealed class Tracked(UsageSession session)
    {
        public UsageSession Session { get; set; } = session;

        public DateTimeOffset? MissingSince { get; set; }

        public bool ConfirmedRaised { get; set; }
    }
}
