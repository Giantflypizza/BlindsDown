using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BlindsDown.Core.Helper;
using BlindsDown.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>
/// [Stage 2 service] The helper's local named pipe (\\.\pipe\BlindsDown.Helper): message mode, one request and
/// one response per message, at most <see cref="Core.Helper.HelperProtocol.MaxMessageBytes"/> each, a few
/// instances, and short timeouts so a stuck client can't block others for long.
/// Security: created with PIPE_REJECT_REMOTE_CLIENTS and an explicit DACL: deny NETWORK; allow SYSTEM and
/// Administrators full control; allow INTERACTIVE users read/write WITHOUT FILE_CREATE_PIPE_INSTANCE (so while the
/// helper runs, nobody else can add instances and squat the name). The owner is SYSTEM, set explicitly; clients
/// accept only SYSTEM or Administrators as owner (see <see cref="HelperClient"/>).
/// The handler runs for every request (raw bytes in, raw bytes out) and must not throw. It may also be given a
/// check that says whether the client is still connected, i.e. still waiting for the answer.
/// <c>forTests</c> uses the current user instead of the service ACL, so tests can run a server unelevated.
/// </summary>
/// <remarks>
/// One request per connection: read one message, answer it, wait until the client hangs up (closing first could
/// discard an answer the client hasn't read yet), close. An empty answer from the handler, or a client that hung up
/// while it waited, closes the connection without one.
/// The pipe name must never lapse while the server runs: with no instance left, a client would be told "not
/// running", and any program could create the name. So one instance always waits for the next client. It is created
/// as soon as the previous one connects; when every instance is busy, the first connection to end creates it while
/// closing its own instance. Whenever no instance exists at all, the next one is created with
/// FILE_FLAG_FIRST_PIPE_INSTANCE, so the server never joins a pipe someone else created in between.
/// While the helper is stopped (an update, a restart after a crash), the name does lapse, and another program could
/// take it: then <see cref="Start"/> fails, the helper logs who holds the name (<see cref="DescribeNameHolder"/>),
/// and the tray app refuses to talk to that program. A restart of the PC gives the name back to the helper, which
/// starts before any program of a signed-in user.
/// Any interactive program can still keep every instance busy for a while (it can as well send Lock or Unlock
/// itself, see SECURITY.md): the short timeouts and freeing an instance as soon as its client hangs up keep honest
/// clients from waiting long.
/// </remarks>
internal sealed class HelperPipeServer : IDisposable
{
    /// <summary>A few clients at once: tray apps in more than one Windows session, their status checks that
    /// overlap a long lock or unlock, and the installer.</summary>
    internal const int MaxInstances = 8;

    private const int BufferBytes = 64 * 1024;
    private static readonly TimeSpan BusyCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly Func<byte[], Func<bool>, byte[]> _handle;
    private readonly bool _forTests;
    private readonly object _gate = new();
    private readonly HashSet<NamedPipeServerStream> _instances = [];
    private readonly HashSet<Task> _connections = [];
    private readonly CancellationTokenSource _stop = new();
    private NativeSecurityDescriptor? _security;
    private NamedPipeServerStream? _listening;
    private TaskCompletionSource _listeningCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lastCreateError;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>A server whose handler only needs the request.</summary>
    public HelperPipeServer(string pipeName, Func<byte[], byte[]> handle, bool forTests = false)
        : this(pipeName, (request, _) => handle(request), forTests)
    {
        ArgumentNullException.ThrowIfNull(handle);
    }

    /// <summary>A server whose handler also gets a check that is true while the client still waits for the answer
    /// (it never throws, and may be called from any thread, even shortly after the handler has returned).</summary>
    public HelperPipeServer(string pipeName, Func<byte[], Func<bool>, byte[]> handle, bool forTests = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _path = HelperPaths.LocalPipePath(pipeName);
        _handle = handle;
        _forTests = forTests;
    }

    /// <summary>How long a client may take to send its request once connected. The tray app and setup send it right
    /// away, so this is short: a client that connects and says nothing frees its instance soon.</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a client may take to take the answer and hang up.</summary>
    internal TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tests only: another security descriptor, e.g. the service's access list without its SYSTEM owner,
    /// which an unelevated test can't set.</summary>
    internal string? SddlForTests { get; init; }

    /// <summary>Creates the first pipe instance and starts accepting clients in the background. Throws
    /// <see cref="IOException"/> when the pipe can't be created, e.g. because another program already uses the name.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acceptLoop is not null)
            {
                throw new InvalidOperationException("The helper's pipe is already listening.");
            }

            _security ??= NativeSecurityDescriptor.FromSddl(
                SddlForTests ?? (_forTests ? HelperPipeAcl.TestSddl(CurrentUser()) : HelperPipeAcl.ServiceSddl));
            EnsureListeningLocked();
            if (_listening is null)
            {
                throw new IOException(CreateErrorText(_lastCreateError), unchecked((int)(0x80070000 | (uint)_lastCreateError)));
            }

            _acceptLoop = Task.Run(AcceptLoopAsync);
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();

            // Closing the instances ends every wait, read and write on them.
            foreach (NamedPipeServerStream instance in _instances)
            {
                instance.Dispose();
            }

            _instances.Clear();
            _listening = null;
            pending = _acceptLoop is null ? [.. _connections] : [.. _connections, _acceptLoop];
        }

        // A request still being handled finishes on its own; a service stop must never wait long for it.
        bool finished;
        try
        {
            finished = Task.WaitAll(pending, StopWait);
        }
        catch (AggregateException)
        {
            finished = true;
        }

        _security?.Dispose();
        if (finished)
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Who holds the pipe name, for the helper's log when it can't create its pipe: "process 1234 in session 1, owned
    /// by an account that isn't SYSTEM: ..." and the like. Connects as a client without sending anything, at
    /// identification level only (the program behind the pipe can learn who connected, but can't act as SYSTEM).
    /// Null when that can't be found out.
    /// </summary>
    internal static string? DescribeNameHolder(string pipeName, TimeSpan timeout)
    {
        HelperPipeServerInfo server;
        try
        {
            server = new HelperClient(pipeName, requireSystemOwner: false).InspectServerAsync(timeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HelperUnavailableException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string process = server.ProcessId is uint id ? $"process {id.ToString(CultureInfo.InvariantCulture)}" : "an unknown process";
        string session = server.SessionId is uint s ? $" in session {s.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        string owner = server.OwnerSid is HelperPipeAcl.LocalSystemSid or HelperPipeAcl.AdministratorsSid
            ? "owned by SYSTEM or Administrators, maybe an earlier copy of the helper that hasn't ended yet"
            : "owned by an account that isn't SYSTEM: another program took the name";
        return $"{process}{session}, {owner}";
    }

    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current account has no SID.");
    }

    /// <summary>True while the client end of a connected instance is open. Never throws: an instance that is
    /// already closed has no client.</summary>
    private static unsafe bool IsClientConnected(SafePipeHandle pipe)
    {
        try
        {
            uint available;
            if (HelperNative.PeekNamedPipe(pipe, null, 0, null, &available, null) != 0)
            {
                return true;
            }

            // Anything else (it shouldn't happen) counts as still there: dropping a request that a client still
            // waits for would refuse what the user asked for, with no reason given.
            return Marshal.GetLastPInvokeError() is not (HelperNative.ErrorBrokenPipe or HelperNative.ErrorNoData or HelperNative.ErrorPipeNotConnected);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string CreateErrorText(int error) => error switch
    {
        HelperNative.ErrorAccessDenied => "Another program already uses the helper's pipe name, or Windows denied creating it.",
        HelperNative.ErrorInvalidOwner => "Windows refused SYSTEM as the pipe's owner: the helper isn't running as SYSTEM.",
        _ => $"Could not create the helper's pipe (Windows error {error}).",
    };

    private static async Task<byte[]?> ReadRequestAsync(NamedPipeServerStream stream, CancellationToken cancel)
    {
        // One byte more than allowed, so an oversized message shows up as "more than the limit".
        byte[] buffer = new byte[HelperProtocol.MaxMessageBytes + 1];
        int total = 0;
        do
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                // The client hung up before sending a whole request.
                return null;
            }

            total += read;
            if (total > HelperProtocol.MaxMessageBytes)
            {
                Log.Warn($"Helper pipe: rejected a request larger than {HelperProtocol.MaxMessageBytes} bytes.");
                return null;
            }
        }
        while (!stream.IsMessageComplete);

        return buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>Makes sure an instance waits for the next client, if Windows allows one more (ERROR_PIPE_BUSY
    /// means every instance is serving a client). Call with <see cref="_gate"/> held.</summary>
    private void EnsureListeningLocked()
    {
        if (_disposed || _listening is not null)
        {
            return;
        }

        NamedPipeServerStream? stream = TryCreateInstanceLocked(out _lastCreateError);
        if (stream is not null)
        {
            _listening = stream;
            _listeningCreated.TrySetResult();
        }
    }

    /// <summary>Null with the Windows error when the instance can't be created. Call with <see cref="_gate"/> held.</summary>
    private unsafe NamedPipeServerStream? TryCreateInstanceLocked(out int error)
    {
        uint openMode = HelperNative.PipeAccessDuplex | HelperNative.FileFlagOverlapped
            | (_instances.Count == 0 ? HelperNative.FileFlagFirstPipeInstance : 0);
        uint pipeMode = HelperNative.PipeTypeMessage | HelperNative.PipeReadModeMessage | HelperNative.PipeRejectRemoteClients;

        nint raw;
        bool addedRef = false;
        try
        {
            _security!.DangerousAddRef(ref addedRef);
            var attributes = new HelperNative.SecurityAttributes
            {
                Length = (uint)sizeof(HelperNative.SecurityAttributes),
                SecurityDescriptor = (void*)_security.DangerousGetHandle(),
                InheritHandle = 0,
            };
            fixed (char* path = _path)
            {
                raw = HelperNative.CreateNamedPipeW(path, openMode, pipeMode, MaxInstances, BufferBytes, BufferBytes, 0, &attributes);
            }

            error = raw == HelperNative.InvalidHandleValue ? Marshal.GetLastPInvokeError() : 0;
        }
        finally
        {
            if (addedRef)
            {
                _security!.DangerousRelease();
            }
        }

        if (raw == HelperNative.InvalidHandleValue)
        {
            return null;
        }

        var handle = new SafePipeHandle(raw, ownsHandle: true);
        try
        {
            var stream = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            _instances.Add(stream);
            return stream;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            handle.Dispose();
            error = ex.HResult & 0xFFFF;
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        CancellationToken stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await AcceptOneAsync(stop).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing expected gets here; if something does, the helper must keep answering (the service would
                // not notice a dead loop and restart it), so log it and carry on after a pause.
                Log.Error("Helper pipe: unexpected error while accepting clients", ex);
                try
                {
                    await Task.Delay(ErrorRetryDelay, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Waits for one client and starts serving it. False when the server is stopping.</summary>
    private async Task<bool> AcceptOneAsync(CancellationToken stop)
    {
        NamedPipeServerStream? listening = await WaitForListeningInstanceAsync(stop).ConfigureAwait(false);
        if (listening is null)
        {
            return false;
        }

        bool connected = false;
        try
        {
            await listening.WaitForConnectionAsync(stop).ConfigureAwait(false);
            connected = true;
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && stop.IsCancellationRequested)
        {
            // Dispose: it cancels first, then closes the instances.
            return false;
        }
        catch (Exception ex)
        {
            // E.g. a client connected and hung up before the connection was accepted. Never let the loop die
            // (the helper would stop answering until the next restart): use a fresh instance.
            Log.Warn($"Helper pipe: a connection failed while being accepted: {ex.Message}");
        }

        if (connected)
        {
            lock (_gate)
            {
                _listening = null;
                EnsureListeningLocked();
            }

            Track(ServeAsync(listening));
        }
        else
        {
            Release(listening);
        }

        return true;
    }

    /// <summary>The instance waiting for the next client, once there is one; null when stopping.</summary>
    private async Task<NamedPipeServerStream?> WaitForListeningInstanceAsync(CancellationToken stop)
    {
        bool reported = false;
        while (true)
        {
            Task created;
            int error;
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                EnsureListeningLocked();
                if (_listening is not null)
                {
                    if (reported)
                    {
                        Log.Info("Helper pipe: listening again.");
                    }

                    return _listening;
                }

                if (_listeningCreated.Task.IsCompleted)
                {
                    _listeningCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                created = _listeningCreated.Task;
                error = _lastCreateError;
            }

            bool busy = error == HelperNative.ErrorPipeBusy;
            if (!busy && !reported)
            {
                Log.Error("Helper pipe: " + CreateErrorText(error) + " Retrying.");
                reported = true;
            }

            // Busy: the first connection to end creates the next instance and wakes this up.
            try
            {
                await created.WaitAsync(busy ? BusyCheckInterval : ErrorRetryDelay, stop).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Look again.
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        try
        {
            // The stream wraps a handle created outside .NET, so tell it the read mode (needed for IsMessageComplete).
            stream.ReadMode = PipeTransmissionMode.Message;
            SafePipeHandle pipe = stream.SafePipeHandle;

            byte[]? request;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                readTimeout.CancelAfter(RequestTimeout);
                request = await ReadRequestAsync(stream, readTimeout.Token).ConfigureAwait(false);
            }

            if (request is null)
            {
                return;
            }

            byte[] response = Handle(request, () => IsClientConnected(pipe));
            if (response.Length == 0 || !IsClientConnected(pipe))
            {
                // Nothing to send, or nobody left to send it to (a client that gave up waiting).
                return;
            }

            if (response.Length > HelperProtocol.MaxMessageBytes)
            {
                Log.Error($"Helper pipe: an answer of {response.Length} bytes is over the limit; not sent.");
                return;
            }

            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            writeTimeout.CancelAfter(AnswerTimeout);
            await stream.WriteAsync(response, writeTimeout.Token).ConfigureAwait(false);

            // Wait for the client to take the answer and hang up (the read ends with 0 bytes).
            _ = await stream.ReadAsync(new byte[1], writeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
            {
                Log.Warn("Helper pipe: a client took too long and was disconnected.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_stop.IsCancellationRequested)
            {
                Log.Warn($"Helper pipe: a connection failed: {ex.Message}");
            }
        }
        finally
        {
            Release(stream);
        }
    }

    private byte[] Handle(byte[] request, Func<bool> clientConnected)
    {
        try
        {
            return _handle(request, clientConnected);
        }
        catch (Exception ex)
        {
            // The contract says handle never throws; if it does anyway, drop this connection, keep the server.
            Log.Error("Helper pipe: the request handler failed", ex);
            return [];
        }
    }

    private void Track(Task connection)
    {
        lock (_gate)
        {
            _connections.Add(connection);
        }

        _ = connection.ContinueWith(
            done =>
            {
                lock (_gate)
                {
                    _connections.Remove(done);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Release(NamedPipeServerStream stream)
    {
        // Under the lock, so the instance count and the pipe's existence always agree when the next instance
        // decides whether it must be the first. If every instance was busy, the next client's instance is created
        // right here, before anything else can close: the name never lapses.
        lock (_gate)
        {
            _instances.Remove(stream);
            if (ReferenceEquals(_listening, stream))
            {
                _listening = null;
            }

            stream.Dispose();
            EnsureListeningLocked();
        }
    }
}
