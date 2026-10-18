using System.Text.Json;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Core.Helper;

/// <summary>
/// The helper's request handler (pure logic, runs on the helper's engine thread): maps requests to
/// <see cref="DeviceLockEngine"/> calls, keeps a bounded list of recent alerts with increasing IDs, and never
/// throws (errors become <c>Ok = false</c> responses). Every response carries the helper's version, the current
/// status, the alerts after the request's <see cref="HelperRequest.SinceEvent"/>, the newest event ID and which run of
/// the helper answered (<see cref="HelperResponse.RunId"/>), and is trimmed so the tray app's
/// <see cref="HelperMessages.ParseResponse"/> always accepts it.
/// </summary>
public sealed class HelperRequestHandler
{
    /// <summary>Alerts kept for clients that ask later (the tray app polls every few seconds).</summary>
    internal const int MaxEventsKept = 100;

    /// <summary>The newest alerts sent in one response; the tray app shows popups, so older ones are stale anyway.</summary>
    internal const int MaxEventsPerResponse = 50;

    private const int MaxErrorLength = 200;

    private const int MaxLoggedSourceLength = 80;

    private readonly DeviceLockEngine _engine;
    private readonly string _helperVersion;
    private readonly List<HelperEvent> _events = [];
    private readonly List<HelperChange> _changes = [];
    private readonly IHelperChangeStore? _store;

    /// <summary>Fingerprints of request IDs already used (also those whose change has left the log): a Lock or Unlock
    /// with one of them is refused, so nobody can replay an ID and pass a change off as the app's own.</summary>
    private readonly HashSet<string> _usedDigests = new(StringComparer.Ordinal);
    private readonly Queue<string> _usedOrder = new();

    /// <summary>A Lock request is being handled (its own narrowing is recorded after it, not from the alert).</summary>
    private bool _inLock;

    /// <summary>Devices the engine said, during the request, the lock no longer turns off.</summary>
    private readonly List<string> _releasedDuringRequest = [];

    /// <summary>The last Lock request (cleared by an Unlock): a narrowing the engine finishes later (a device that
    /// couldn't be listed then, or a change a restart of the helper interrupted) belongs to it. Kept on disk with the
    /// change log, so it is still known after a restart.</summary>
    private (string? Digest, string? Source) _lastChanger;
    private readonly Lock _gate = new();
    private long _lastEventId;

    /// <summary>The ID before this run's first alert or change: everything after it is this run's. Sent as
    /// <see cref="HelperResponse.RunId"/>.</summary>
    private readonly long _runStartId;

    /// <summary>
    /// Event IDs continue from the current time in milliseconds, so they keep increasing when the helper restarts
    /// (e.g. after an update): a tray app that asks for alerts "since" an ID from before the restart still gets
    /// the new ones.
    /// </summary>
    /// <param name="engine">The engine whose alerts are kept.</param>
    /// <param name="helperVersion">Sent in every response.</param>
    /// <param name="store">Where the change log is kept between runs of the helper (null: memory only).</param>
    public HelperRequestHandler(DeviceLockEngine engine, string helperVersion, IHelperChangeStore? store = null)
        : this(engine, helperVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), store)
    {
    }

    /// <param name="engine">The engine whose alerts are kept.</param>
    /// <param name="helperVersion">Sent in every response.</param>
    /// <param name="lastEventId">The ID before the first event (tests start at 0).</param>
    /// <param name="store">Where the change log is kept between runs of the helper (null: memory only).</param>
    internal HelperRequestHandler(DeviceLockEngine engine, string helperVersion, long lastEventId, IHelperChangeStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _helperVersion = HelperMessages.Clip(helperVersion);
        _store = store;
        HelperChangeLog kept = LoadLog(store);
        _changes.AddRange(kept.Changes.TakeLast(MaxEventsKept));
        foreach (HelperChange change in _changes)
        {
            RememberUsed(change.RequestDigest);
        }

        // Known before the engine's first alert: the service creates the handler before the engine starts, and the start
        // may finish a narrowing the last lock request began (the helper stopped half-way through it). Its ID can't be
        // used again either, also when the request changed nothing.
        _lastChanger = (kept.LastLockDigest, kept.LastLockSource);
        RememberUsed(kept.LastLockDigest);

        // IDs keep increasing across a restart of the helper, also when the clock went back: each run starts above every
        // ID an earlier run handed out (alerts included), so no client's position can be ahead of a later run's changes.
        _lastEventId = Math.Max(lastEventId, Math.Max(_changes.Count > 0 ? _changes[^1].Id : 0, kept.HighestId));
        _runStartId = _lastEventId;
        if (_runStartId > kept.HighestId)
        {
            // This run's start is handed out with its first answer: on disk first, so a later run starts above it even if
            // this one records nothing.
            Save();
        }

        engine.AlertRaised += OnAlert;
    }

    /// <summary>
    /// A line for the helper's log: after a lock request, devices the lock had turned off are on (a request that makes
    /// the lock cover less), with who the request says it is from. The engine's urgent alert tells the user; this
    /// tells whoever reads the log what asked for it.
    /// </summary>
    public event Action<string>? Logged;

    public HelperResponse Handle(HelperRequest request)
    {
        try
        {
            return Fit(Run(request));
        }
        catch (Exception ex)
        {
            // Never throws: an exception would leave the tray app waiting for an answer that never comes.
            return new HelperResponse
            {
                Ok = false,
                Error = HelperMessages.Clip("The helper failed: " + ex.Message, MaxErrorLength),
                HelperVersion = _helperVersion,
                LastEventId = LastEventId(),
                RunId = _runStartId,
            };
        }
    }

    /// <summary>
    /// One whole pipe exchange, raw bytes in and out (for the pipe server): parses and validates the request,
    /// handles it, serializes the answer. A malformed request gets an <c>Ok = false</c> answer saying why, and
    /// never reaches the engine. Never throws.
    /// </summary>
    public byte[] HandleMessage(byte[] request)
    {
        HelperRequest? parsed = HelperMessages.ParseRequest(request, out string? error);
        HelperResponse response = parsed is not null
            ? Handle(parsed)
            : new HelperResponse
            {
                Ok = false,
                Error = HelperMessages.Clip(error ?? "The request is not valid.", MaxErrorLength),
                HelperVersion = _helperVersion,
                LastEventId = LastEventId(),
                RunId = _runStartId,
            };

        try
        {
            return HelperMessages.SerializeResponse(response);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or JsonException)
        {
            return HelperMessages.SerializeResponse(new HelperResponse { Ok = false, Error = "The helper could not write its answer.", HelperVersion = _helperVersion, RunId = _runStartId });
        }
    }

    private HelperResponse Run(HelperRequest? request)
    {
        if (request is null)
        {
            return Respond(ok: false, "The request is empty.", since: long.MaxValue, sinceRun: null);
        }

        if (request.V != HelperProtocol.Version)
        {
            return Respond(ok: false, $"Protocol version {request.V} is not supported (this helper speaks version {HelperProtocol.Version}).", request);
        }

        switch (request.Command)
        {
            case HelperCommand.Hello:
            case HelperCommand.Status:
                return Respond(ok: true, error: null, request);
            case HelperCommand.Lock:
                if (request.Lock is null)
                {
                    return Respond(ok: false, "A lock request needs lock options.", request);
                }

                if (UsedBefore(request.RequestId) is string usedLock)
                {
                    return Respond(ok: false, usedLock, request);
                }

                DeviceLockStatus before = _engine.Status;
                bool hadLock = _engine.HasLock;
                string? digest = Digest(request.RequestId);

                // On disk before the engine acts: if the helper stops half-way, its next start finishes the change, and
                // that is still this request's, not another program's.
                SetLastChanger((digest, request.Source));

                _releasedDuringRequest.Clear();
                _inLock = true;
                try
                {
                    _engine.Lock(request.Lock);
                }
                finally
                {
                    _inLock = false;
                }

                List<string> turnedOn = TurnedOn(before, _engine.Status);
                LogTurnedOn(turnedOn, request.Source);
                List<string> released = [.. _releasedDuringRequest.Except(turnedOn, StringComparer.OrdinalIgnoreCase)];
                if (!hadLock && _engine.HasLock)
                {
                    AddChange(HelperChangeKind.Locked, digest, request.Source, [], []);
                }
                else if (hadLock && (turnedOn.Count > 0 || released.Count > 0))
                {
                    AddChange(HelperChangeKind.Narrowed, digest, request.Source, turnedOn, released);
                }
                else
                {
                    RememberUsed(digest);
                }

                return Respond(ok: true, error: null, request);
            case HelperCommand.Unlock:
                if (UsedBefore(request.RequestId) is string usedUnlock)
                {
                    return Respond(ok: false, usedUnlock, request);
                }

                bool wasLocked = _engine.HasLock;
                SetLastChanger(default);
                List<string> wereOff = [.. _engine.Status.Devices.Where(d => d.Protection == DeviceProtection.DeviceDisabled).Select(d => d.Name)];
                RestoreReport? report = _engine.Unlock();
                if (wasLocked)
                {
                    AddChange(HelperChangeKind.Unlocked, Digest(request.RequestId), request.Source, wereOff, []);
                }
                else
                {
                    RememberUsed(Digest(request.RequestId));
                }

                return Respond(ok: true, error: null, request, report);
            default:
                return Respond(ok: false, "Unknown command.", request);
        }
    }

    private HelperResponse Respond(bool ok, string? error, HelperRequest request, RestoreReport? report = null) =>
        Respond(ok, error, request.SinceEvent, request.SinceRun, report);

    private HelperResponse Respond(bool ok, string? error, long since, long? sinceRun, RestoreReport? report = null)
    {
        lock (_gate)
        {
            // Newer than any alert this helper raised: the ID comes from an earlier run of the helper whose clock was
            // ahead (the time was corrected since). Or the client says it comes from another run, and it is past where
            // this run started: alerts aren't kept across a restart, so after the clock went back a little this run may
            // already have handed out IDs past it, and comparing would skip this run's first changes. Either way nothing
            // from before this run is newer than the client's position (no earlier change has an ID past this run's
            // start), so it gets all of this run's alerts and changes, and is told its position started over.
            bool ahead = since > _lastEventId;
            bool fromAnotherRun = sinceRun is long run && run > 0 && run != _runStartId && since > _runStartId;
            bool reset = false;
            if ((ahead || fromAnotherRun) && since != long.MaxValue)
            {
                since = _runStartId;
                reset = true;
            }

            return new HelperResponse
            {
                Ok = ok,
                Error = error,
                HelperVersion = _helperVersion,
                Status = _engine.Status,
                Events = [.. _events.Where(e => e.Id > since).TakeLast(MaxEventsPerResponse)],
                Changes = [.. _changes.Where(c => c.Id > since).TakeLast(MaxEventsPerResponse)],
                SinceReset = reset,
                LastEventId = _lastEventId,
                RunId = _runStartId,
                Report = report,
            };
        }
    }

    /// <summary>
    /// Trims the response for the wire (see <see cref="HelperMessages.ForWire"/>) and, if it is still larger than
    /// <see cref="HelperProtocol.MaxMessageBytes"/>, drops the oldest events until it fits.
    /// </summary>
    private HelperResponse Fit(HelperResponse response)
    {
        HelperResponse wire = HelperMessages.ForWire(response);
        while (HelperMessages.SerializeResponse(wire).Length > HelperProtocol.MaxMessageBytes)
        {
            if (wire.Events.Count == 0 && wire.Changes.Count > 1)
            {
                // Changes matter more than alerts (they tell the tray app about another program's unlock): drop them last.
                wire = wire with { Changes = [.. wire.Changes.Skip((wire.Changes.Count + 1) / 2)] };
                continue;
            }

            if (wire.Events.Count == 0)
            {
                return new HelperResponse
                {
                    Ok = false,
                    Error = "The helper's answer was too large to send.",
                    HelperVersion = _helperVersion,
                    LastEventId = wire.LastEventId,
                    RunId = wire.RunId,
                };
            }

            wire = wire with { Events = [.. wire.Events.Skip((wire.Events.Count + 1) / 2)] };
        }

        return wire;
    }

    /// <summary>The devices a lock request turned back on: off because of the lock before it, on after it.</summary>
    private static List<string> TurnedOn(DeviceLockStatus before, DeviceLockStatus after) =>
    [
        .. after.Devices
            .Where(d => d.Protection is not (DeviceProtection.DeviceDisabled or DeviceProtection.NotPresent or DeviceProtection.DisabledByUser)
                && before.Devices.Any(b => b.Protection == DeviceProtection.DeviceDisabled && string.Equals(b.Id, d.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Name),
    ];

    /// <summary>Records a change to the lock with the request that made it (see <see cref="HelperChange"/>), and keeps the
    /// log on disk.</summary>
    private void AddChange(HelperChangeKind kind, string? digest, string? source, List<string> turnedOn, List<string> released)
    {
        lock (_gate)
        {
            _lastEventId++;
            _changes.Add(new HelperChange(_lastEventId, kind, digest, source, turnedOn, released, DateTimeOffset.UtcNow));
            if (_changes.Count > MaxEventsKept)
            {
                _changes.RemoveRange(0, _changes.Count - MaxEventsKept);
            }

            RememberUsed(digest);
        }

        Save();
    }

    /// <summary>Sets the last Lock request (see <see cref="_lastChanger"/>) and keeps it on disk when it changed.</summary>
    private void SetLastChanger((string? Digest, string? Source) changer)
    {
        if (_lastChanger == changer)
        {
            return;
        }

        _lastChanger = changer;
        Save();
    }

    /// <summary>Writes the change log and the last Lock request to the store.</summary>
    private void Save()
    {
        if (_store is null)
        {
            return;
        }

        HelperChangeLog log;
        lock (_gate)
        {
            log = new HelperChangeLog([.. _changes], _lastChanger.Digest, _lastChanger.Source) { HighestId = _lastEventId };
        }

        try
        {
            _store.Save(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory anyway; only a restart of the helper before the next save would lose it.
            Logged?.Invoke("Could not save the lock change log: " + ex.Message);
        }
    }

    private static string? Digest(string? requestId) => requestId is { Length: > 0 } id ? HelperProtocol.DigestOf(id) : null;

    /// <summary>Why a request ID can't be used again, or null when it is new (or there is none).</summary>
    private string? UsedBefore(string? requestId)
    {
        string? digest = Digest(requestId);
        lock (_gate)
        {
            return digest is not null && _usedDigests.Contains(digest) ? "That request ID was already used." : null;
        }
    }

    /// <summary>Call with <see cref="_gate"/> held, or before the handler is shared.</summary>
    private void RememberUsed(string? digest)
    {
        if (digest is null || !_usedDigests.Add(digest))
        {
            return;
        }

        _usedOrder.Enqueue(digest);
        while (_usedOrder.Count > 1000)
        {
            _usedDigests.Remove(_usedOrder.Dequeue());
        }
    }

    private static HelperChangeLog LoadLog(IHelperChangeStore? store)
    {
        try
        {
            HelperChangeLog? log = store?.Load();
            return log is null ? HelperChangeLog.Empty : log with { Changes = log.Changes ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return HelperChangeLog.Empty;
        }
    }

    /// <summary>Logs the devices a lock request turned back on, with who the request says it is from.</summary>
    private void LogTurnedOn(List<string> turnedOn, string? source)
    {
        if (turnedOn.Count > 0)
        {
            // The source is the client's own claim, already free of control characters (see HelperMessages).
            string from = string.IsNullOrEmpty(source) ? "a client that didn't say who it is" : $"\"{HelperMessages.Clip(source, MaxLoggedSourceLength)}\"";
            Logged?.Invoke($"After a lock request from {from}, these devices are no longer turned off: {string.Join(", ", turnedOn)}.");
        }
    }

    private void OnAlert(EngineAlert alert)
    {
        if (alert.Message.StartsWith(DeviceLockEngine.NarrowedAlertPrefix, StringComparison.Ordinal))
        {
            List<string> names = [.. alert.DeviceName.Split(", ", StringSplitOptions.RemoveEmptyEntries)];
            if (_inLock)
            {
                // Recorded with the request once it is done.
                _releasedDuringRequest.AddRange(names);
            }
            else
            {
                // The engine finished a narrowing later (a device it couldn't list during the request, or one an
                // interrupted change left at a restart): still a change, made by the last lock request (also one from
                // before a restart of the helper; none after an unlock, so the app treats it as another program's).
                AddChange(HelperChangeKind.Narrowed, _lastChanger.Digest, _lastChanger.Source, [], names);
            }
        }

        lock (_gate)
        {
            _lastEventId++;
            _events.Add(new HelperEvent(_lastEventId, alert.Kind, alert.DeviceName, alert.Message, alert.IsUrgent));
            if (_events.Count > MaxEventsKept)
            {
                _events.RemoveRange(0, _events.Count - MaxEventsKept);
            }
        }

        // Alerts aren't kept across a restart, but their IDs are: the next run starts above them (the engine limits how
        // often it alerts, so this is a small write now and then).
        Save();
    }

    private long LastEventId()
    {
        lock (_gate)
        {
            return _lastEventId;
        }
    }
}

/// <summary>
/// What the helper keeps between its runs: the change log, and the last lock request (until an unlock), so a narrowing the
/// engine finishes after a restart (a change the restart interrupted, a device that couldn't be listed) is still told as
/// that request's and not as another program's.
/// </summary>
/// <param name="Changes">The newest changes, oldest first.</param>
/// <param name="LastLockDigest">The last lock request's <see cref="HelperChange.RequestDigest"/> (null: none since the
/// last unlock, or it had no ID).</param>
/// <param name="LastLockSource">Who the last lock request said it was from.</param>
public sealed record HelperChangeLog(IReadOnlyList<HelperChange> Changes, string? LastLockDigest = null, string? LastLockSource = null)
{
    public static HelperChangeLog Empty { get; } = new([]);

    /// <summary>The highest alert or change ID this helper has handed out (0: not known); the next run starts above it.</summary>
    public long HighestId { get; init; }
}

/// <summary>Keeps the helper's change log between its runs (a restart of the helper must not hide a change from the app).</summary>
public interface IHelperChangeStore
{
    /// <summary>The log as last saved (empty when there is none).</summary>
    HelperChangeLog Load();

    void Save(HelperChangeLog log);
}

/// <summary>The change log in a file next to the helper's journal (same protected folder), written atomically.</summary>
public sealed class FileHelperChangeStore(string path) : IHelperChangeStore
{
    /// <summary>The newest readable copy of the log. A copy that isn't valid JSON is skipped, entries that are incomplete
    /// or of an unknown kind are dropped (one of them would make every answer unreadable for the app), and with no
    /// usable copy the log is empty.</summary>
    public HelperChangeLog Load()
    {
        foreach (string text in IO.AtomicFile.ReadCandidates(path))
        {
            try
            {
                HelperChangeLog? log = JsonSerializer.Deserialize(text, Json.HelperChangeLogJsonContext.Default.HelperChangeLog);
                if (log is not null)
                {
                    return log with
                    {
                        Changes = [.. (log.Changes ?? []).Where(c => c is not null && Enum.IsDefined(c.Kind) && c.TurnedOn is not null && c.Released is not null)],
                    };
                }
            }
            catch (JsonException)
            {
                // Try the next copy.
            }
        }

        return HelperChangeLog.Empty;
    }

    public void Save(HelperChangeLog log) =>
        IO.AtomicFile.WriteAllText(path, JsonSerializer.Serialize(log, Json.HelperChangeLogJsonContext.Default.HelperChangeLog));
}
