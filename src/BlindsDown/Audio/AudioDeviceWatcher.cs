using BlindsDown.Core.Devices;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// Watches microphones: endpoints added/removed/state changes (IMMNotificationClient),
/// default device changes, and mute changes on every usable capture endpoint
/// (IAudioEndpointVolumeCallback). All events are raised ON THE ENGINE THREAD.
///
/// Mute notifications are per endpoint object, and an endpoint's volume object stops working when the
/// endpoint changes state or the audio service restarts. So the subscriptions are rebuilt whenever an
/// endpoint changes, and a periodic check (every few seconds) rebuilds broken ones and reports endpoint
/// changes that a lost notification would otherwise hide. An endpoint whose subscription is rebuilt is
/// reported as changed too, because a mute change while it had none went unnoticed.
///
/// <see cref="MuteChanged"/> is raised only when the mute state changes (volume-only notifications are
/// dropped). It is not a complete history: listeners must read the current state rather than count events.
/// </summary>
internal sealed class AudioDeviceWatcher : IDisposable
{
    private const string Component = "Microphone watcher";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly EngineThread _engine;
    private readonly EndpointEnumerator _enumerator;
    private readonly ProblemLog _problems = new(Component);
    private readonly Dictionary<string, MuteSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, EndpointState> _knownStates = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _checkTimer;
    private bool _started;
    private volatile bool _disposed;

    public AudioDeviceWatcher(EngineThread engine)
    {
        _engine = engine;

        // The enumerator keeps the notification client (and so this watcher) alive while it is registered.
        _enumerator = new EndpointEnumerator(Component, new EndpointNotificationClient(engine, OnEndpointNotified, OnCaptureDefaultNotified));
    }

    /// <summary>An endpoint was added, removed, or changed state. Argument: endpoint ID.</summary>
    public event Action<string>? EndpointChanged;

    /// <summary>(endpointId, muted, causedByUs). causedByUs is true when the change carried
    /// <see cref="CoreAudioMicBackend.EventContext"/>.</summary>
    public event Action<string, bool, bool>? MuteChanged;

    public event Action? DefaultDeviceChanged;

    /// <summary>Number of endpoints with a live mute subscription (for diagnostics; read on the engine thread).</summary>
    internal int WatchedEndpointCount => _subscriptions.Count;

    /// <summary>Starts watching. Call on the engine thread (a call from another thread is queued there).</summary>
    public void Start()
    {
        if (!_engine.IsCurrentThread)
        {
            _engine.Post(Start);
            return;
        }

        if (_disposed || _started)
        {
            return;
        }

        _started = true;

        // The first use of the enumerator registers the notification client, so nothing that changes during
        // the initial scan is missed.
        Refresh(notifiedId: null, raiseEvents: false);
        _checkTimer = new System.Threading.Timer(_ => EngineDispatch.PostFromCallback(_engine, PeriodicCheck), null, CheckInterval, CheckInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _checkTimer, null)?.Dispose();
        EngineDispatch.RunTeardown(_engine, Teardown, Component);
    }

    private void OnEndpointNotified(string endpointId)
    {
        if (_disposed || !_started || !IsCaptureEndpoint(endpointId))
        {
            return;
        }

        // Whatever happened, the old volume object may now be stale: subscribe afresh.
        Unsubscribe(endpointId);
        Refresh(endpointId, raiseEvents: true);
    }

    private void OnCaptureDefaultNotified()
    {
        if (!_disposed && _started)
        {
            DefaultDeviceChanged?.Invoke();
        }
    }

    private void OnMuteNotified(string endpointId, VolumeNotification notification)
    {
        if (_disposed)
        {
            return;
        }

        // The same notification also reports volume changes (apps with automatic gain control send many per
        // second); only pass on actual mute changes. A subscription starts with "unknown", so the first
        // notification after (re)subscribing is always passed on.
        _subscriptions.TryGetValue(endpointId, out MuteSubscription? subscription);
        if (subscription is not null && subscription.LastMuted == notification.Muted)
        {
            return;
        }

        MuteChanged?.Invoke(endpointId, notification.Muted, notification.EventContext == CoreAudioMicBackend.EventContext);

        // A handler may already have changed the state (the lock engine re-mutes at once), and Windows does not
        // promise to deliver notifications in order. If the real state no longer matches what was just reported,
        // forget it, so that the next notification is passed on even if it repeats the reported state: otherwise
        // an unmute that arrives right after our own re-mute could be dropped as "no change".
        if (subscription is not null && _subscriptions.TryGetValue(endpointId, out MuteSubscription? current) && ReferenceEquals(current, subscription))
        {
            bool stillCurrent = subscription.Volume.Value.GetMute(out int muted) >= 0 && (muted != 0) == notification.Muted;
            subscription.LastMuted = stillCurrent ? notification.Muted : null;
        }
    }

    /// <summary>
    /// The periodic safety check (every <see cref="CheckInterval"/>; internal so device tests can run it at once).
    /// Rebuilds broken mute subscriptions and reports endpoint changes that a lost notification hid. Engine thread only.
    /// </summary>
    internal void PeriodicCheck()
    {
        if (_disposed || !_started)
        {
            return;
        }

        bool serviceFailed = false;
        foreach ((string id, MuteSubscription subscription) in _subscriptions.ToList())
        {
            int hr = subscription.Volume.Value.GetMute(out _);
            if (hr < 0)
            {
                serviceFailed |= CoreAudioErrors.IsServiceFailure(hr);
                Unsubscribe(id);
            }
        }

        if (serviceFailed)
        {
            // The audio service restarted: endpoint notifications registered before it are gone too.
            _enumerator.Reset();
        }

        Refresh(notifiedId: null, raiseEvents: true);
    }

    /// <summary>
    /// Re-reads all capture endpoints, (re)subscribes to mute changes where possible, and raises
    /// <see cref="EndpointChanged"/> for every endpoint whose state differs from the last scan or whose mute
    /// subscription had to be made afresh, plus <paramref name="notifiedId"/>.
    /// </summary>
    private void Refresh(string? notifiedId, bool raiseEvents)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            changed = _enumerator.WithRecovery(Synchronize);
            _problems.Resolve("scan");
        }
        catch (CoreAudioException ex)
        {
            _problems.Report("scan", ex.Message);
        }

        if (!raiseEvents)
        {
            return;
        }

        if (notifiedId is not null)
        {
            changed.Add(notifiedId);
        }

        foreach (string id in changed)
        {
            EndpointChanged?.Invoke(id);
        }
    }

    private HashSet<string> Synchronize()
    {
        List<ComRef<IMMDevice>> devices = _enumerator.OpenCaptureDevices(CoreAudioConstants.AllStates);
        try
        {
            var current = new Dictionary<string, (EndpointState State, IMMDevice Device)>(StringComparer.OrdinalIgnoreCase);
            foreach (ComRef<IMMDevice> device in devices)
            {
                if (EndpointReader.GetId(device.Value) is { } id && EndpointReader.GetState(device.Value) is { } state)
                {
                    current[id] = (state, device.Value);
                }
            }

            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, (EndpointState state, _)) in current)
            {
                if (!_knownStates.TryGetValue(id, out EndpointState known) || known != state)
                {
                    changed.Add(id);
                }
            }

            changed.UnionWith(_knownStates.Keys.Where(id => !current.ContainsKey(id)));
            _knownStates = current.ToDictionary(kv => kv.Key, kv => kv.Value.State, StringComparer.OrdinalIgnoreCase);

            foreach (string id in _subscriptions.Keys.ToList())
            {
                if (changed.Contains(id) || !current.TryGetValue(id, out var entry) || !IsActivatable(entry.State))
                {
                    Unsubscribe(id);
                }
            }

            foreach ((string id, (EndpointState state, IMMDevice device)) in current)
            {
                // A mute change while an endpoint had no working subscription (it broke, or could not be made)
                // went unnoticed, so an endpoint that is subscribed afresh counts as changed: listeners re-check it.
                if (IsActivatable(state) && !_subscriptions.ContainsKey(id) && Subscribe(id, device))
                {
                    changed.Add(id);
                }
            }

            return changed;
        }
        finally
        {
            foreach (ComRef<IMMDevice> device in devices)
            {
                device.Dispose();
            }
        }
    }

    /// <summary>Subscribes to the endpoint's mute changes. False (and logged) when that is not possible right now.</summary>
    private bool Subscribe(string endpointId, IMMDevice device)
    {
        string topic = "mute notifications for " + endpointId;
        ComRef<IAudioEndpointVolume>? volume = EndpointReader.TryActivate<IAudioEndpointVolume>(device, out int hr);
        if (volume is null)
        {
            _problems.Report(topic, $"Could not open the volume control of {endpointId}: {CoreAudioErrors.Describe(hr)}");
            return false;
        }

        var sink = new MuteNotificationSink(_engine, endpointId, OnMuteNotified);
        hr = volume.Value.RegisterControlChangeNotify(sink);
        if (hr < 0)
        {
            volume.Dispose();
            _problems.Report(topic, $"Could not subscribe to mute changes of {endpointId}: {CoreAudioErrors.Describe(hr)}");
            return false;
        }

        _problems.Resolve(topic);
        _subscriptions[endpointId] = new MuteSubscription(volume, sink);
        return true;
    }

    private void Unsubscribe(string endpointId)
    {
        if (_subscriptions.Remove(endpointId, out MuteSubscription? subscription))
        {
            // Fails harmlessly when the endpoint or the audio service is already gone.
            _ = subscription.Volume.Value.UnregisterControlChangeNotify(subscription.Sink);
            subscription.Volume.Dispose();
        }
    }

    /// <summary>
    /// Endpoint notifications cover render endpoints too. The ID format is undocumented, so the direction
    /// comes from IMMEndpoint; an endpoint that no longer exists counts as capture only if we knew it as one.
    /// </summary>
    private bool IsCaptureEndpoint(string endpointId)
    {
        if (_knownStates.ContainsKey(endpointId))
        {
            return true;
        }

        try
        {
            return _enumerator.WithRecovery(() =>
            {
                using ComRef<IMMDevice>? device = _enumerator.OpenDevice(endpointId);
                return device is not null && EndpointReader.GetDataFlow(device.Value) == DataFlow.Capture;
            });
        }
        catch (CoreAudioException ex)
        {
            _problems.Report("scan", ex.Message);
            return false;
        }
    }

    private void Teardown()
    {
        // Start may have created the timer after Dispose looked for it.
        Interlocked.Exchange(ref _checkTimer, null)?.Dispose();
        foreach (string id in _subscriptions.Keys.ToList())
        {
            Unsubscribe(id);
        }

        _enumerator.Dispose();
        _knownStates.Clear();
    }

    private static bool IsActivatable(EndpointState state) => (state & CoreAudioConstants.ActivatableStates) != 0;

    /// <summary>A live mute subscription. The sink is kept here so it stays alive while Windows holds it.</summary>
    private sealed class MuteSubscription(ComRef<IAudioEndpointVolume> volume, MuteNotificationSink sink)
    {
        public ComRef<IAudioEndpointVolume> Volume { get; } = volume;

        public MuteNotificationSink Sink { get; } = sink;

        /// <summary>The mute state from the last notification; null until the first one.</summary>
        public bool? LastMuted { get; set; }
    }
}
