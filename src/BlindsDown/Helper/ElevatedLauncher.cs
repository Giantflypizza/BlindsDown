using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using BlindsDown.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>The only elevated things BlindsDown ever starts: itself, with one of these options.</summary>
internal enum ElevatedTask
{
    InstallHelper,
    UpdateHelper,
    UninstallHelper,
}

/// <summary>
/// [Stage 2 service] Starts THIS exe (Environment.ProcessPath, never another path) elevated with the fixed option
/// for an <see cref="ElevatedTask"/>, optionally followed by "--request-id" and an ID of 32 lowercase hex characters
/// (ShellExecuteExW "runas", SEE_MASK_NOCLOSEPROCESS), so Windows shows one UAC prompt, and waits for it to finish
/// without blocking the UI thread.
/// Returns the exit code, or null when the user said No at the UAC prompt (ERROR_CANCELLED).
/// </summary>
/// <remarks>
/// The start itself (which waits while the permission prompt is shown) runs on its own short-lived STA thread, the
/// apartment ShellExecuteEx wants, so BlindsDown's windows keep painting meanwhile. If Windows can't start the
/// program at all, the result is 1 ("failed"), with the reason in BlindsDown's log, like a failed setup run.
/// </remarks>
internal static class ElevatedLauncher
{
    /// <summary>Hands the setup run the ID of its unlock requests (see <see cref="HelperSetup.SetupRequestIds"/>).</summary>
    internal const string RequestIdOption = "--request-id";

    /// <summary>The form of a request ID the tray app hands a setup run: what <c>Guid.ToString("N")</c> gives.</summary>
    internal const int RequestIdLength = 32;

    /// <param name="task">What to run.</param>
    /// <param name="owner">The window the permission prompt belongs to.</param>
    /// <param name="requestId">Null, or the ID (32 lowercase hex characters, <c>Guid.ToString("N")</c>) the setup run
    /// sends with its unlock requests, so the tray app recognises the unlock of its own install or removal and doesn't
    /// announce it as another program's. The caller registers <see cref="HelperSetup.SetupRequestIds"/> of it as its own
    /// before calling (see there).</param>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is not 32 lowercase hex characters; nothing was
    /// started (thrown right away, not from the returned task).</exception>
    public static Task<int?> RunAsync(ElevatedTask task, IWin32Window? owner, string? requestId = null)
    {
        string arguments = Arguments(task, requestId);
        return RunAsync(arguments, Arguments(task), owner?.Handle ?? 0);
    }

    /// <param name="arguments">The checked command line.</param>
    /// <param name="option">What the log says was run: the option without the request ID. The ID is only for the
    /// helper and the tray app; an unused one (the prompt was declined) in a log someone shares could be sent by
    /// another program and pass for this app's.</param>
    /// <param name="ownerWindow">The window the permission prompt belongs to (read on the caller's thread).</param>
    private static async Task<int?> RunAsync(string arguments, string option, nint ownerWindow)
    {
        SafeProcessHandle? process;
        try
        {
            process = await StartOnPromptThreadAsync(arguments, ownerWindow).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            Log.Error($"Could not start BlindsDown {option} with administrator rights", ex);
            return 1;
        }

        if (process is null)
        {
            Log.Info($"BlindsDown {option}: the administrator permission prompt was declined.");
            return null;
        }

        using (process)
        {
            int exitCode = await WaitForExitAsync(process).ConfigureAwait(false);
            Log.Info($"BlindsDown {option} finished with exit code {exitCode}.");
            return exitCode;
        }
    }

    internal static string Arguments(ElevatedTask task) => task switch
    {
        ElevatedTask.InstallHelper => "--install-helper",
        ElevatedTask.UpdateHelper => "--update-helper",
        ElevatedTask.UninstallHelper => "--uninstall-helper",
        _ => throw new ArgumentOutOfRangeException(nameof(task)),
    };

    /// <summary>The task's option, followed by <c>--request-id</c> and the ID when there is one.</summary>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is not 32 lowercase hex characters.</exception>
    internal static string Arguments(ElevatedTask task, string? requestId)
    {
        if (requestId is null)
        {
            return Arguments(task);
        }

        // Checked here as well as in SafeShell, so a bad ID is refused before a thread or prompt is started for it.
        return IsRequestId(requestId)
            ? $"{Arguments(task)} {RequestIdOption} {requestId}"
            : throw new ArgumentException($"A setup request ID must be {RequestIdLength} lowercase hex characters.", nameof(requestId));
    }

    /// <summary>True for exactly <see cref="RequestIdLength"/> characters 0-9 and a-f (no uppercase, no dashes).</summary>
    internal static bool IsRequestId([NotNullWhen(true)] string? requestId) =>
        requestId is { Length: RequestIdLength } && requestId.All(char.IsAsciiHexDigitLower);

    private static Task<SafeProcessHandle?> StartOnPromptThreadAsync(string arguments, nint ownerWindow)
    {
        var started = new TaskCompletionSource<SafeProcessHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                started.SetResult(SafeShell.StartSelfElevated(arguments, ownerWindow));
            }
            catch (Exception ex)
            {
                started.SetException(ex);
            }
        })
        {
            Name = "BlindsDown permission prompt",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task;
    }

    /// <summary>Waits on a thread-pool wait (no thread is blocked) and returns the exit code (1 if unreadable).</summary>
    private static async Task<int> WaitForExitAsync(SafeProcessHandle process)
    {
        using var exited = new ProcessExitWaitHandle(process);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            exited,
            (_, _) => done.TrySetResult(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
        try
        {
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }

        return ExitCode(process);
    }

    private static unsafe int ExitCode(SafeProcessHandle process)
    {
        uint code;
        if (HelperNative.GetExitCodeProcess(process.DangerousGetHandle(), &code) == 0)
        {
            return 1;
        }

        return unchecked((int)code);
    }

    /// <summary>Lets the thread pool wait on a process handle it doesn't own (the caller keeps the process open).</summary>
    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
