using BlindsDown.Core.Devices;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;
using BlindsDown.Usage;

namespace BlindsDown.Audio;

/// <summary>
/// Live "which app is using a microphone" from Windows audio sessions
/// (IAudioSessionManager2 on every active capture endpoint, session notifications + state events).
/// Never opens a capture stream itself. Events are raised ON THE ENGINE THREAD.
///
/// An app that captures from a microphone owns an audio session on that endpoint, and the session is
/// "active" exactly while a stream runs. Reading sessions needs no admin rights and, unlike Windows' usage
/// registry, names the process and the device. Captures from a virtual microphone carry the name of the
/// adapter that relays it, e.g. Discord on "SteelSeries Sonar - Microphone" is reported with
/// ViaApp = "SteelSeries Sonar Virtual Audio Device".
/// </summary>
internal sealed class MicSessionMonitor : IDisposable
{
    public const string SourceName = "audio-sessions";
    private const string Component = "Microphone sessions";

    private readonly EngineThread _engine;
    private readonly IProcessIdentityResolver _identities;
    private readonly EndpointEnumerator _enumerator;
    private readonly SessionChangeSink _sessionSink;
    private readonly ProblemLog _problems = new(Component);
    private readonly Dictionary<string, CaptureEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private volatile bool _disposed;

    public MicSessionMonitor(EngineThread engine, IProcessIdentityResolver identities)
    {
        _engine = engine;
        _identities = identities;

        // Both sinks are kept alive by fields of this monitor (the enumerator holds the endpoint client).
        _sessionSink = new SessionChangeSink(engine, Refresh);
        _enumerator = new EndpointEnumerator(Component, new EndpointNotificationClient(engine, _ => Refresh(), onCaptureDefaultChanged: null));
    }

    /// <summary>Something changed; call <see cref="GetActiveSignals"/>. May also fire when nothing relevant changed.</summary>
    public event Action? Changed;

    /// <summary>Number of sessions currently followed on all active microphones (for diagnostics; read on the engine thread).</summary>
    internal int TrackedSessionCount => _endpoints.Values.Sum(e => e.Sessions.Count);

    /// <summary>Call on the engine thread (a call from another thread is queued there).</summary>
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
        Resync();
    }

    /// <summary>Re-scans endpoints/sessions (safety net, call every ~2 s) and returns the active captures.
    /// Call on the engine thread. Empty until <see cref="Start"/> ran.</summary>
    public IReadOnlyList<UsageSignal> GetActiveSignals()
    {
        Resync();
        var signals = new List<UsageSignal>();
        var reported = new HashSet<(int ProcessId, string EndpointId)>();
        foreach (CaptureEndpoint endpoint in _endpoints.Values)
        {
            foreach (CaptureSession session in endpoint.Sessions.Values)
            {
                if (session.AppProcessId is not { } processId
                    || session.Control.Value.GetState(out AudioSessionState state) < 0
                    || state != AudioSessionState.Active
                    || !reported.Add((processId, endpoint.Id)))
                {
                    continue;
                }

                signals.Add(new UsageSignal(
                    CaptureKind.Microphone,
                    _identities.Resolve(processId),
                    processId,
                    endpoint.Id,
                    endpoint.Name,
                    endpoint.ViaApp));
            }
        }

        return signals;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EngineDispatch.RunTeardown(_engine, Teardown, Component);
    }

    /// <summary>A Windows notification arrived (already moved to the engine thread).</summary>
    private void Refresh()
    {
        if (_disposed || !_started)
        {
            return;
        }

        Resync();
        Changed?.Invoke();
    }

    private void Resync()
    {
        if (_disposed || !_started)
        {
            return;
        }

        try
        {
            _enumerator.WithRecovery(SyncEndpoints);
            _problems.Resolve("scan");
        }
        catch (CoreAudioException ex)
        {
            _problems.Report("scan", ex.Message);
            if (CoreAudioErrors.IsServiceFailure(ex.HResult))
            {
                // Every session manager from before the outage is dead; start from scratch once it is back.
                CloseAllEndpoints();
            }
        }
    }

    private void SyncEndpoints()
    {
        List<ComRef<IMMDevice>> devices = _enumerator.OpenCaptureDevices(EndpointState.Active);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ComRef<IMMDevice> device in devices)
            {
                if (EndpointReader.GetId(device.Value) is not { } id)
                {
                    continue;
                }

                seen.Add(id);
                if (_endpoints.TryGetValue(id, out CaptureEndpoint? endpoint))
                {
                    if (SyncSessions(endpoint))
                    {
                        continue;
                    }

                    // Its session manager stopped working (endpoint reset, or the audio service restarted).
                    CloseEndpoint(endpoint);
                    _endpoints.Remove(id);
                }

                if (OpenEndpoint(id, device.Value) is { } opened)
                {
                    _endpoints[id] = opened;
                }
            }
        }
        finally
        {
            foreach (ComRef<IMMDevice> device in devices)
            {
                device.Dispose();
            }
        }

        foreach (CaptureEndpoint gone in _endpoints.Values.Where(e => !seen.Contains(e.Id)).ToList())
        {
            CloseEndpoint(gone);
            _endpoints.Remove(gone.Id);
        }
    }

    private CaptureEndpoint? OpenEndpoint(string endpointId, IMMDevice device)
    {
        EndpointProperties properties = EndpointReader.ReadProperties(device);
        string topic = "sessions of " + endpointId;
        ComRef<IAudioSessionManager2>? manager = EndpointReader.TryActivate<IAudioSessionManager2>(device, out int hr);
        if (manager is null)
        {
            _problems.Report(topic, $"Could not read the audio sessions of \"{properties.DisplayName}\": {CoreAudioErrors.Describe(hr)}");
            return null;
        }

        // For a virtual microphone the adapter name says which app relays it ("SteelSeries Sonar Virtual Audio Device").
        string? viaApp = DeviceIds.IsVirtualHardware(properties.HardwareInstanceId) ? DeviceIds.ShortVirtualDeviceName(properties.AdapterName) : null;
        var endpoint = new CaptureEndpoint(endpointId, properties.DisplayName, viaApp, manager);

        // Windows only delivers "session created" notifications to a manager whose session list was read
        // first (GetSessionEnumerator + GetCount), so read it before registering.
        if (!SyncSessions(endpoint))
        {
            CloseEndpoint(endpoint);
            return null;
        }

        hr = manager.Value.RegisterSessionNotification(_sessionSink);
        endpoint.NotificationRegistered = hr >= 0;
        if (!endpoint.NotificationRegistered)
        {
            _problems.Report(topic, $"Could not subscribe to new audio sessions on \"{endpoint.Name}\": {CoreAudioErrors.Describe(hr)}. They are still found by the periodic check.");
        }

        // A session that started between the first read and the registration is picked up here.
        _ = SyncSessions(endpoint);
        return endpoint;
    }

    /// <summary>Brings the endpoint's session list up to date. False when its session manager no longer works.</summary>
    private bool SyncSessions(CaptureEndpoint endpoint)
    {
        int hr = endpoint.Manager.Value.GetSessionEnumerator(out nint pointer);
        using ComRef<IAudioSessionEnumerator>? sessions = hr >= 0 ? ComRef.TryTake<IAudioSessionEnumerator>(pointer) : null;
        int count = 0;
        if (sessions is not null)
        {
            hr = sessions.Value.GetCount(out count);
        }

        if (sessions is null || hr < 0)
        {
            int reason = sessions is null && hr >= 0 ? CoreAudioErrors.NoInterface : hr;
            _problems.Report("sessions of " + endpoint.Id, $"Could not list the audio sessions of \"{endpoint.Name}\": {CoreAudioErrors.Describe(reason)}");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (sessions.Value.GetSession(i, out nint sessionPointer) < 0)
            {
                continue;
            }

            ComRef<IAudioSessionControl2>? control = ComRef.TryTake<IAudioSessionControl2>(sessionPointer);
            string? key = control is null ? null : TrackingKey(control.Value);
            if (control is null || key is null || !seen.Add(key) || endpoint.Sessions.ContainsKey(key))
            {
                // Not worth following, or a session we already follow (Windows hands out a new reference each time).
                control?.Dispose();
                continue;
            }

            endpoint.Sessions[key] = OpenSession(control);
        }

        foreach (string gone in endpoint.Sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            CloseSession(endpoint.Sessions[gone]);
            endpoint.Sessions.Remove(gone);
        }

        _problems.Resolve("sessions of " + endpoint.Id);
        return true;
    }

    /// <summary>
    /// The session's instance ID, or null for a session not worth following. An expired session has no
    /// streams left (its app stopped capturing or exited); holding it would only keep it, and with it every
    /// exited app's session, alive. If the app captures again, the next scan picks the session up again.
    /// </summary>
    private static string? TrackingKey(IAudioSessionControl2 control)
    {
        if (control.GetState(out AudioSessionState state) < 0 || state == AudioSessionState.Expired)
        {
            return null;
        }

        return control.GetSessionInstanceIdentifier(out nint text) >= 0 ? CoTaskMemText.Take(text) : null;
    }

    /// <summary>
    /// The ID of the app process that owns a session, or null for a session that is Windows itself (the system
    /// sounds session, PID 0) or whose owner can't be read. GetProcessId may return AUDCLNT_S_NO_SINGLE_PROCESS:
    /// that is a SUCCESS code (the session spans several processes) and the PID is still the one that created
    /// the session, so it counts like S_OK. IsSystemSoundsSession returns S_OK only for the system sounds session.
    /// </summary>
    internal static int? AppProcessId(int processIdResult, uint processId, int systemSoundsResult) =>
        processIdResult >= 0 && processId != 0 && systemSoundsResult != CoreAudioErrors.SOk ? (int)processId : null;

    private CaptureSession OpenSession(ComRef<IAudioSessionControl2> control)
    {
        int processIdResult = control.Value.GetProcessId(out uint processId);
        int? appProcessId = AppProcessId(processIdResult, processId, control.Value.IsSystemSoundsSession());
        bool eventsRegistered = appProcessId is not null && control.Value.RegisterAudioSessionNotification(_sessionSink) >= 0;
        return new CaptureSession(control, appProcessId, eventsRegistered);
    }

    private void CloseSession(CaptureSession session)
    {
        if (session.EventsRegistered)
        {
            // Fails harmlessly when the session or the audio service is already gone.
            _ = session.Control.Value.UnregisterAudioSessionNotification(_sessionSink);
        }

        session.Control.Dispose();
    }

    private void CloseEndpoint(CaptureEndpoint endpoint)
    {
        foreach (CaptureSession session in endpoint.Sessions.Values)
        {
            CloseSession(session);
        }

        endpoint.Sessions.Clear();
        if (endpoint.NotificationRegistered)
        {
            _ = endpoint.Manager.Value.UnregisterSessionNotification(_sessionSink);
        }

        endpoint.Manager.Dispose();
    }

    private void CloseAllEndpoints()
    {
        foreach (CaptureEndpoint endpoint in _endpoints.Values)
        {
            CloseEndpoint(endpoint);
        }

        _endpoints.Clear();
    }

    private void Teardown()
    {
        CloseAllEndpoints();
        _enumerator.Dispose();
    }

    /// <summary>One active capture endpoint and the sessions on it, keyed by session instance ID.</summary>
    private sealed class CaptureEndpoint(string id, string name, string? viaApp, ComRef<IAudioSessionManager2> manager)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string? ViaApp { get; } = viaApp;

        public ComRef<IAudioSessionManager2> Manager { get; } = manager;

        public bool NotificationRegistered { get; set; }

        public Dictionary<string, CaptureSession> Sessions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class CaptureSession(ComRef<IAudioSessionControl2> control, int? appProcessId, bool eventsRegistered)
    {
        public ComRef<IAudioSessionControl2> Control { get; } = control;

        /// <summary>The owning app's process ID; null for the system sounds session and sessions without a process.</summary>
        public int? AppProcessId { get; } = appProcessId;

        public bool EventsRegistered { get; } = eventsRegistered;
    }
}
