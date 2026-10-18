using System.ComponentModel;
using System.ServiceProcess;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Time;
using BlindsDown.Devices.Pnp;
using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>
/// The helper's Windows service object (see <see cref="HelperService"/>). Windows calls OnStart and OnStop on its
/// own threads; everything that touches devices runs on the helper's <see cref="EngineThread"/>, and the pipe hands
/// requests there one at a time.
/// </summary>
/// <remarks>
/// OnStart returns at once (Windows waits for it): the device lock starts on the engine thread. If it can't start
/// (or the pipe can't be created), the service keeps running, answers requests with the reason, and tries again
/// every few seconds, so a passing problem at boot doesn't need a restart. Stopping keeps a lock: devices stay off
/// and the journal makes the next start apply it again. There is deliberately no OnCustomCommand: the pipe is the
/// helper's only way in.
/// </remarks>
internal sealed class HelperServiceHost : ServiceBase
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(3);

    /// <summary>How often a request waiting for the device thread checks that its client is still there.</summary>
    private static readonly TimeSpan ClientCheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the helper takes to find out who holds its pipe name, when another program does.</summary>
    private static readonly TimeSpan NameHolderCheckTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan StopStepTimeout = TimeSpan.FromSeconds(5);

    private const int MaxLoggedTextLength = 64;

    private readonly object _pipeGate = new();
    private volatile EngineThread? _engineThread;
    private System.Threading.Timer? _timer;
    private HelperPipeServer? _pipe;
    private volatile bool _stopping;
    private bool _stopped;
    private int _tickQueued;

    /// <summary>The newest alert ID in an answer from the request handler, for the helper's own answers.</summary>
    private long _lastEventId;

    // Engine thread only.
    private DeviceLockEngine? _engine;
    private HelperRequestHandler? _handler;
    private PnpNotificationWatcher? _watcher;
    private string? _startupError;
    private string? _lastTickError;
    private bool _pipeErrorLogged;

    public HelperServiceHost()
    {
        ServiceName = HelperProtocol.ServiceName;

        // Never write to the Event Log (and so never need its package at run time); the helper has its own log.
        AutoLog = false;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        CanHandlePowerEvent = false;
        CanHandleSessionChangeEvent = false;
    }

    /// <summary>False when Windows' service manager never started this service.</summary>
    public bool WasStarted { get; private set; }

    protected override void OnStart(string[] args)
    {
        WasStarted = true;
        Log.Info($"BlindsDown camera helper {AppVersion.Text} starting.");
        var engineThread = new EngineThread("BlindsDown helper", ex => Log.Error("Helper device thread error", ex));
        _engineThread = engineThread;
        engineThread.Post(StartOnEngine);
        _timer = new System.Threading.Timer(_ => QueueTick(engineThread), null, ReconcileInterval, ReconcileInterval);
    }

    protected override void OnStop() => Shutdown("stopped");

    protected override void OnShutdown() => Shutdown("Windows is shutting down");

    protected override void Dispose(bool disposing)
    {
        if (disposing && WasStarted)
        {
            Shutdown("closing");
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// One of the helper's own failure answers, for a request the request handler never answered. It keeps the
    /// client's place in the alert list (its <see cref="HelperRequest.SinceEvent"/>), or else gives the newest alert ID
    /// the helper has sent: the tray app takes its place from every answer, and 0 would make it show every alert the
    /// helper still keeps once more.
    /// </summary>
    internal static HelperResponse Failure(string error, HelperRequest? request, long lastEventId) => new()
    {
        Ok = false,
        Error = error,
        HelperVersion = AppVersion.Text,
        LastEventId = request is { SinceEvent: > 0 } ? request.SinceEvent : lastEventId,
    };

    private static byte[] Serialize(HelperResponse response)
    {
        try
        {
            byte[] bytes = HelperMessages.SerializeResponse(response);
            if (bytes.Length <= HelperProtocol.MaxMessageBytes)
            {
                return bytes;
            }

            Log.Error($"An answer of {bytes.Length} bytes is over the limit; sending an error instead.");
            return HelperMessages.SerializeResponse(Failure("The helper's answer was too large.", null, response.LastEventId));
        }
        catch (Exception ex)
        {
            // An empty answer makes the pipe close the connection; the tray app reports the helper as unavailable.
            Log.Error("Could not write an answer", ex);
            return [];
        }
    }

    /// <summary>Client-supplied text, made safe for a one-line log entry.</summary>
    private static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "?";
        }

        string clean = new([.. text.Take(MaxLoggedTextLength).Select(c => char.IsControl(c) ? ' ' : c)]);
        return text.Length > MaxLoggedTextLength ? clean + "..." : clean;
    }

    /// <summary>At most one check waits in the queue: while a long lock or unlock runs, checks don't pile up.</summary>
    private void QueueTick(EngineThread engineThread)
    {
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0)
        {
            engineThread.Post(Tick);
        }
    }

    private void StartOnEngine()
    {
        TryStartEngine();
        TryStartPipe();
    }

    /// <summary>Every <see cref="ReconcileInterval"/>, on the engine thread: the engine's safety net (retries
    /// vetoed disables, catches missed notifications), and retries of whatever failed to start.</summary>
    private void Tick()
    {
        Volatile.Write(ref _tickQueued, 0);
        if (_stopping)
        {
            return;
        }

        try
        {
            if (_engine is null)
            {
                TryStartEngine();
            }
            else
            {
                _engine.Reconcile();
            }

            _lastTickError = null;
        }
        catch (Exception ex)
        {
            // Logged once per distinct error, not every three seconds.
            if (_lastTickError != ex.Message)
            {
                Log.Error("Device check failed", ex);
                _lastTickError = ex.Message;
            }
        }

        if (Volatile.Read(ref _pipe) is null)
        {
            TryStartPipe();
        }
    }

    private void TryStartEngine()
    {
        try
        {
            var engine = new DeviceLockEngine(new CfgMgrPnpBackend(), new FileDeviceJournalStore(HelperPaths.JournalFile), SystemClock.Instance);
            engine.AlertRaised += alert => Log.Info($"Alert ({alert.Kind}): {alert.DeviceName}: {alert.Message}");

            // Created before Initialize, so alerts raised while a journaled lock is re-applied reach the tray app. Its
            // change log is kept on disk: a change made while the tray app was closed, or just before the helper
            // restarted (an update), is still reported to it, and the request IDs behind it can't be used again.
            var handler = new HelperRequestHandler(engine, AppVersion.Text, new FileHelperChangeStore(HelperPaths.ChangeLogFile));

            // Who made a lock cover less, and a change log that couldn't be saved: both belong in the helper's log.
            handler.Logged += Log.Warn;

            // Notifications before the first device list, so no change in between is missed. They are posted to this
            // thread, so they run after Initialize.
            PnpNotificationWatcher? watcher = StartWatcher(engine);
            bool locked;
            try
            {
                locked = engine.Initialize();
            }
            catch
            {
                watcher?.Dispose();
                throw;
            }

            if (engine.JournalWasCorrupt)
            {
                Log.Warn("The lock record could not be read, so it is unknown which devices the helper turned off. They come back at the next restart.");
            }

            Log.Info(locked ? "A lock recorded before the restart is in effect again." : "No lock is active.");
            _engine = engine;
            _handler = handler;
            _watcher = watcher;
            _startupError = null;
        }
        catch (Exception ex)
        {
            if (_startupError != ex.Message)
            {
                Log.Error("The device lock could not start; retrying every few seconds", ex);
            }

            _startupError = ex.Message;
        }
    }

    /// <summary>Null when notifications can't start: the check every few seconds still catches every change.</summary>
    private PnpNotificationWatcher? StartWatcher(DeviceLockEngine engine)
    {
        var watcher = new PnpNotificationWatcher(_engineThread!);

        // Only the engine in use: a notification queued for an engine whose start failed must not act on devices.
        watcher.Changed += () =>
        {
            if (ReferenceEquals(_engine, engine))
            {
                engine.OnDevicesChanged();
            }
        };
        try
        {
            watcher.Start();
            return watcher;
        }
        catch (Exception ex)
        {
            watcher.Dispose();
            Log.Error("Device notifications could not start; the check every few seconds still catches changes", ex);
            return null;
        }
    }

    private void TryStartPipe()
    {
        if (_stopping)
        {
            return;
        }

        var pipe = new HelperPipeServer(HelperProtocol.PipeName, HandleRequest);
        try
        {
            pipe.Start();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            pipe.Dispose();
            if (!_pipeErrorLogged)
            {
                Log.Error("The helper's pipe could not be created; retrying every few seconds", ex);

                // While the helper was stopped (an update, a restart after a crash), any program could take the name.
                // The tray app refuses to talk to it; this says who it is. A restart of the PC gives the name back:
                // the helper starts before any program of a signed-in user.
                if (HelperPipeServer.DescribeNameHolder(HelperProtocol.PipeName, NameHolderCheckTimeout) is string holder)
                {
                    Log.Warn($@"\\.\pipe\{HelperProtocol.PipeName} is held by {holder}.");
                }

                _pipeErrorLogged = true;
            }

            return;
        }

        lock (_pipeGate)
        {
            if (_stopping)
            {
                pipe.Dispose();
                return;
            }

            _pipe = pipe;
        }

        _pipeErrorLogged = false;
        Log.Info($@"Listening on \\.\pipe\{HelperProtocol.PipeName}.");
    }

    /// <summary>Runs on a pipe connection's thread; never throws. <paramref name="clientConnected"/> says whether the
    /// client still waits for the answer.</summary>
    private byte[] HandleRequest(byte[] raw, Func<bool> clientConnected)
    {
        HelperResponse response;
        HelperRequest? request = null;
        try
        {
            request = HelperMessages.ParseRequest(raw, out string? error);
            EngineThread? engineThread = _engineThread;
            if (request is null)
            {
                Log.Warn($"Rejected a request that could not be read: {Printable(error)}");
                response = Failure(error ?? "The request could not be read.", null);
            }
            else if (_stopping || engineThread is null)
            {
                response = Failure("The helper is stopping.", request);
            }
            else
            {
                response = RunOnEngine(engineThread, request, clientConnected);
            }
        }
        catch (Exception ex)
        {
            Log.Error("A request failed", ex);
            response = Failure("The helper ran into an error: " + ex.Message, request);
        }

        return Serialize(response);
    }

    /// <summary>
    /// Hands a request to the engine thread and waits for its answer, at most <see cref="HelperTimeouts.AnswerWithin"/>
    /// (see there for the three possible answers). A request that hasn't started when the wait ends, or whose client
    /// hangs up before it starts, is dropped, never left to run later: its client was told the helper is busy, or
    /// isn't listening any more, so a lock or unlock it asked for minutes ago must not suddenly happen. A request that
    /// already started runs to the end (stopping half way would leave devices half changed); the next status shows
    /// its result.
    /// </summary>
    private HelperResponse RunOnEngine(EngineThread engineThread, HelperRequest request, Func<bool> clientConnected)
    {
        TurnResult<HelperResponse> turn = PipeRequestTurn.Run(
            engineThread, () => HandleOnEngine(request), clientConnected, HelperTimeouts.AnswerWithin, ClientCheckInterval);

        // Status and Hello clients wait only a few seconds, so they often leave while a long lock or unlock runs:
        // not worth a log line every time.
        bool changes = request.Command is HelperCommand.Lock or HelperCommand.Unlock;
        switch (turn.Outcome)
        {
            case TurnOutcome.Done:
                return turn.Value!;
            case TurnOutcome.Dropped:
                if (changes)
                {
                    Log.Warn(turn.ClientLeft
                        ? $"A {request.Command} request's client stopped waiting before its turn came; it is dropped and will not run."
                        : $"A {request.Command} request waited too long for the device thread; it is dropped and will not run.");
                }

                return Failure(HelperTimeouts.BusyAnswer, request);
            default:
                if (changes)
                {
                    Log.Warn(turn.ClientLeft
                        ? $"A {request.Command} request's client stopped waiting; the request keeps running."
                        : $"A {request.Command} request is taking long; it keeps running.");
                }

                return Failure(HelperTimeouts.StillWorkingAnswer, request);
        }
    }

    private HelperResponse Failure(string error, HelperRequest? request) => Failure(error, request, Interlocked.Read(ref _lastEventId));

    private HelperResponse HandleOnEngine(HelperRequest request)
    {
        if (_handler is null)
        {
            return Failure(
                _startupError is null
                    ? "The helper is still starting."
                    : "The helper could not start its device lock: " + _startupError,
                request);
        }

        bool changes = request.Command is HelperCommand.Lock or HelperCommand.Unlock;
        if (changes)
        {
            Log.Info($"{request.Command} requested by {Printable(request.Source)} (BlindsDown {Printable(request.ClientVersion)}).");
        }

        HelperResponse response = _handler.Handle(request);
        Interlocked.Exchange(ref _lastEventId, response.LastEventId);
        if (changes)
        {
            Log.Info(response.Ok
                ? $"{request.Command} done: {response.Status?.State ?? LockState.Unlocked}."
                : $"{request.Command} failed: {response.Error}");
        }

        return response;
    }

    private void Shutdown(string why)
    {
        HelperPipeServer? pipe;
        lock (_pipeGate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _stopping = true;
            pipe = _pipe;
            _pipe = null;
        }

        Log.Info($"Helper stopping ({why}). A lock stays in effect and applies again at the next start.");

        // Order: no new requests, no new checks, no new device events, then the thread (which first finishes
        // what is already queued, for at most a few seconds).
        pipe?.Dispose();
        _timer?.Dispose();
        _timer = null;
        EngineThread? engineThread = _engineThread;
        if (engineThread is not null)
        {
            try
            {
                engineThread.Invoke(
                    () =>
                    {
                        _watcher?.Dispose();
                        _watcher = null;
                        return true;
                    },
                    StopStepTimeout);
            }
            catch (System.TimeoutException)
            {
                Log.Warn("The device thread did not finish in time; stopping anyway.");
            }

            engineThread.Dispose();
            _engineThread = null;
        }

        Log.Info("Helper stopped.");
    }
}
