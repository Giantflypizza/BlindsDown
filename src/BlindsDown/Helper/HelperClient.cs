using System.Runtime.InteropServices;
using System.Security.Principal;
using BlindsDown.Core.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>
/// [Stage 2 service] The tray app's side of the helper pipe. Opens ONLY the local pipe "\\.\pipe\" + name with
/// CreateFileW (not NamedPipeClientStream, which can reach other machines; BlindsDown never uses the network),
/// with SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION so the helper can't act as the user, and checks that the
/// pipe's owner is SYSTEM before sending anything (a pipe squatted by a normal program is refused).
/// Thread-safe; one request at a time.
/// </summary>
/// <remarks>
/// "Owned by SYSTEM" means owned by SYSTEM or by Administrators: the helper names SYSTEM as its pipe's owner, and
/// Administrators (a LocalSystem process's default owner) is accepted too; a normal program can name neither. The
/// second check is the server's session: services run in session 0, where a program started by a signed-in user
/// never runs. The session comes from GetNamedPipeServerSessionId, because ProcessIdToSessionId needs to open the
/// server process, which Windows doesn't allow a normal user for a SYSTEM service (checked on Windows 11: access
/// denied).
/// The exchange runs on a thread-pool thread with overlapped I/O, so every step honours the timeout and the
/// cancellation token; I/O that is given up is cancelled and waited for before its buffers are released.
/// </remarks>
internal sealed class HelperClient(string pipeName = HelperProtocol.PipeName, bool requireSystemOwner = true)
{
    /// <summary>WaitNamedPipe is called in slices this long, so a cancellation is noticed quickly.</summary>
    private const int BusyWaitSliceMs = 250;

    private readonly string _path = HelperPaths.LocalPipePath(pipeName);
    private readonly object _oneAtATime = new();

    /// <summary>Sends one request. Throws <see cref="HelperUnavailableException"/> when the helper isn't installed,
    /// isn't running, doesn't answer within <paramref name="timeout"/>, or isn't who it should be.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<HelperResponse> SendAsync(HelperRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        byte[] answer = await SendRawAsync(HelperMessages.SerializeRequest(request), timeout, cancellationToken).ConfigureAwait(false);
        HelperResponse? response;
        string? error;
        try
        {
            response = HelperMessages.ParseResponse(answer, out error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The parser reports problems by returning null; anything it throws still means "unreadable answer".
            throw new HelperUnavailableException("The camera helper sent an answer BlindsDown can't read.", ex);
        }

        return response ?? throw new HelperUnavailableException(
            "The camera helper sent an answer BlindsDown can't read" + (string.IsNullOrEmpty(error) ? "." : $": {error}"));
    }

    /// <summary>The transport alone: one message out, one message back. Sizes are not checked on the way out (the
    /// server enforces its limit); the answer may be at most <see cref="HelperProtocol.MaxMessageBytes"/>.</summary>
    internal Task<byte[]> SendRawAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long deadline = Deadline(timeout);
        return Task.Run(() => Exchange(request, deadline, cancellationToken), cancellationToken);
    }

    /// <summary>Connects and reports who runs the pipe, without checking it or sending anything.</summary>
    internal Task<HelperPipeServerInfo> InspectServerAsync(TimeSpan timeout)
    {
        long deadline = Deadline(timeout);
        return Task.Run(() =>
        {
            nint pipe = Connect(deadline, CancellationToken.None);
            try
            {
                return Inspect(pipe);
            }
            finally
            {
                _ = HelperNative.CloseHandle(pipe);
            }
        });
    }

    private static long Deadline(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, TimeSpan.FromHours(1));
        return Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    }

    private static int RemainingMs(long deadline) => (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);

    private static HelperUnavailableException TimedOut() => new("The camera helper didn't answer in time.", HelperUnavailableKind.TimedOut);

    /// <summary>The timeout ran out before anything was sent (see <see cref="HelperUnavailableKind.Busy"/>).</summary>
    private static HelperUnavailableException NotSent() =>
        new("The camera helper was busy and didn't take the request, so nothing changed.", HelperUnavailableKind.Busy);

    private static HelperUnavailableException Broken(int error) => error switch
    {
        HelperNative.ErrorBrokenPipe or HelperNative.ErrorNoData or HelperNative.ErrorPipeNotConnected =>
            new HelperUnavailableException("The camera helper closed the connection without answering."),
        _ => new HelperUnavailableException($"The connection to the camera helper failed (Windows error {error})."),
    };

    private static unsafe HelperPipeServerInfo Inspect(nint pipe)
    {
        string? owner = null;
        void* ownerSid = null;
        void* descriptor = null;
        if (HelperNative.GetSecurityInfo(pipe, HelperNative.SeKernelObject, HelperNative.OwnerSecurityInformation, &ownerSid, null, null, null, &descriptor) == 0)
        {
            try
            {
                owner = ownerSid is null ? null : new SecurityIdentifier((nint)ownerSid).Value;
            }
            finally
            {
                _ = HelperNative.LocalFree((nint)descriptor);
            }
        }

        uint session;
        uint processId;
        bool hasSession = HelperNative.GetNamedPipeServerSessionId(pipe, &session) != 0;
        bool hasProcess = HelperNative.GetNamedPipeServerProcessId(pipe, &processId) != 0;
        return new HelperPipeServerInfo(owner, hasSession ? session : null, hasProcess ? processId : null);
    }

    private static void VerifyServer(nint pipe)
    {
        HelperPipeServerInfo server = Inspect(pipe);
        string? problem = HelperPipeAcl.ServerProblem(server.OwnerSid, server.SessionId ?? uint.MaxValue);
        if (problem is not null)
        {
            string process = server.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            Log.Warn($"Refused to talk to the helper pipe: {problem} (server process {process}).");
            throw new HelperUnavailableException(ImpostorMessage(server.ProcessId), HelperUnavailableKind.Impostor);
        }
    }

    /// <summary>What the user is told when another program holds the helper's pipe name. Repairing (updating) the
    /// helper can't help then, because the new helper can't get the name either; a restart of the PC can.</summary>
    internal static string ImpostorMessage(uint? processId) =>
        "Another program"
        + (processId is uint id ? $" (process {id.ToString(System.Globalization.CultureInfo.InvariantCulture)})" : string.Empty)
        + " is answering in the camera helper's place, so BlindsDown refused to talk to it. Restart the PC to give the helper its place back.";

    private static unsafe void SetMessageMode(nint pipe)
    {
        uint mode = HelperNative.PipeReadModeMessage;
        if (HelperNative.SetNamedPipeHandleState(pipe, &mode, null, null) == 0)
        {
            throw Broken(Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe uint Write(nint pipe, NativeOverlapped* overlapped, ManualResetEvent completed, byte[] message, long deadline, CancellationToken cancel)
    {
        fixed (byte* source = message)
        {
            completed.Reset();
            int result = HelperNative.WriteFile(pipe, source, (uint)message.Length, null, overlapped);
            _ = Finish(result, pipe, overlapped, completed, deadline, cancel, out uint written);
            return written;
        }
    }

    private static unsafe byte[] Read(nint pipe, NativeOverlapped* overlapped, ManualResetEvent completed, long deadline, CancellationToken cancel)
    {
        // One byte more than allowed, so an oversized answer shows up as "more than the limit".
        byte[] buffer = new byte[HelperProtocol.MaxMessageBytes + 1];
        int total = 0;
        fixed (byte* target = buffer)
        {
            while (true)
            {
                completed.Reset();
                int result = HelperNative.ReadFile(pipe, target + total, (uint)(buffer.Length - total), null, overlapped);
                int status = Finish(result, pipe, overlapped, completed, deadline, cancel, out uint read);
                total += (int)read;
                if (total > HelperProtocol.MaxMessageBytes)
                {
                    throw new HelperUnavailableException("The camera helper's answer was too large.");
                }

                if (status != HelperNative.ErrorMoreData)
                {
                    break;
                }
            }
        }

        return total > 0
            ? buffer.AsSpan(0, total).ToArray()
            : throw new HelperUnavailableException("The camera helper sent an empty answer.");
    }

    /// <summary>
    /// Completes an overlapped ReadFile/WriteFile that returned <paramref name="callResult"/>. Returns 0, or
    /// ERROR_MORE_DATA when a read got only part of a message. Throws for anything else, including a timeout or a
    /// cancellation, after cancelling the I/O and waiting until Windows no longer uses the caller's buffers.
    /// </summary>
    private static unsafe int Finish(int callResult, nint pipe, NativeOverlapped* overlapped, ManualResetEvent completed, long deadline, CancellationToken cancel, out uint transferred)
    {
        if (callResult == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == HelperNative.ErrorIoPending)
            {
                int remaining = RemainingMs(deadline);
                int signalled = remaining == 0 ? WaitHandle.WaitTimeout : WaitHandle.WaitAny([completed, cancel.WaitHandle], remaining);
                if (signalled != 0)
                {
                    _ = HelperNative.CancelIoEx(pipe, overlapped);
                    uint ignored;
                    _ = HelperNative.GetOverlappedResult(pipe, overlapped, &ignored, 1);
                    cancel.ThrowIfCancellationRequested();
                    throw TimedOut();
                }
            }
            else if (error != HelperNative.ErrorMoreData)
            {
                throw Broken(error);
            }
        }

        uint count;
        if (HelperNative.GetOverlappedResult(pipe, overlapped, &count, 0) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            transferred = count;
            return error == HelperNative.ErrorMoreData ? error : throw Broken(error);
        }

        transferred = count;
        return 0;
    }

    private unsafe byte[] Exchange(byte[] request, long deadline, CancellationToken cancel)
    {
        if (!Monitor.TryEnter(_oneAtATime, RemainingMs(deadline)))
        {
            throw NotSent();
        }

        try
        {
            nint pipe = Connect(deadline, cancel);
            try
            {
                // Nothing is sent before the server is known to be the real helper.
                if (requireSystemOwner)
                {
                    VerifyServer(pipe);
                }

                SetMessageMode(pipe);
                return Transfer(pipe, request, deadline, cancel);
            }
            finally
            {
                _ = HelperNative.CloseHandle(pipe);
            }
        }
        finally
        {
            Monitor.Exit(_oneAtATime);
        }
    }

    private static unsafe byte[] Transfer(nint pipe, byte[] request, long deadline, CancellationToken cancel)
    {
        using var completed = new ManualResetEvent(false);
        NativeOverlapped* overlapped = null;
        try
        {
            overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            overlapped->EventHandle = completed.SafeWaitHandle.DangerousGetHandle();
            _ = Write(pipe, overlapped, completed, request, deadline, cancel);
            return Read(pipe, overlapped, completed, deadline, cancel);
        }
        finally
        {
            // Every I/O above has completed, or was cancelled and waited for, so Windows no longer uses this.
            NativeMemory.Free(overlapped);
        }
    }

    private unsafe nint Connect(long deadline, CancellationToken cancel)
    {
        fixed (char* path = _path)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                nint pipe = HelperNative.CreateFileW(
                    path,
                    HelperNative.GenericRead | HelperNative.GenericWrite,
                    0,
                    null,
                    HelperNative.OpenExisting,
                    HelperNative.FileFlagOverlapped | HelperNative.SecuritySqosPresent | HelperNative.SecurityIdentification,
                    0);
                if (pipe != HelperNative.InvalidHandleValue)
                {
                    return pipe;
                }

                int error = Marshal.GetLastPInvokeError();
                switch (error)
                {
                    case HelperNative.ErrorFileNotFound:
                        throw new HelperUnavailableException("The camera helper isn't running.");
                    case HelperNative.ErrorAccessDenied:
                        throw new HelperUnavailableException("Windows didn't allow BlindsDown to talk to the camera helper.");
                    case HelperNative.ErrorPipeBusy:
                        break;
                    default:
                        throw new HelperUnavailableException($"Could not reach the camera helper (Windows error {error}).");
                }

                // Every instance is busy with another client: wait for one to free up, then try again.
                int remaining = RemainingMs(deadline);
                if (remaining == 0)
                {
                    throw NotSent();
                }

                _ = HelperNative.WaitNamedPipeW(path, (uint)Math.Min(remaining, BusyWaitSliceMs));
            }
        }
    }
}

/// <summary>Who runs a pipe: its owner's SID, and the Windows session and process ID of its server.</summary>
internal sealed record HelperPipeServerInfo(string? OwnerSid, uint? SessionId, uint? ProcessId);

/// <summary>Why the helper couldn't be used, where the caller may want to act on it.</summary>
internal enum HelperUnavailableKind
{
    /// <summary>Not installed, not running, the connection failed, or the answer was unreadable. (The last two can
    /// happen after the request was sent; that is rare, and the helper's next status shows what it did.)</summary>
    Unavailable,

    /// <summary>The request was sent, but no answer came within the caller's timeout: it may still be carried out
    /// (see <see cref="HelperTimeouts"/>: with <see cref="HelperTimeouts.ChangeTimeout"/>, Lock and Unlock get here
    /// only when connecting itself took long, or the helper hangs).</summary>
    TimedOut,

    /// <summary>The caller's timeout ran out before the request could be sent: every instance of the helper's pipe
    /// stayed busy with other clients, or this client's previous request was still running. The helper never saw
    /// it, so it is never carried out.</summary>
    Busy,

    /// <summary>Another program holds the helper's pipe name; nothing was sent to it. A restart of the PC fixes it.</summary>
    Impostor,
}

internal sealed class HelperUnavailableException : Exception
{
    public HelperUnavailableException()
    {
    }

    public HelperUnavailableException(string message)
        : base(message)
    {
    }

    public HelperUnavailableException(string message, HelperUnavailableKind kind)
        : base(message)
    {
        Kind = kind;
    }

    public HelperUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HelperUnavailableKind Kind { get; } = HelperUnavailableKind.Unavailable;
}
