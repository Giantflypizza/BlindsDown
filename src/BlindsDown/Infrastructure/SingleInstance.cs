namespace BlindsDown.Infrastructure;

internal enum InstanceCommand
{
    Show,
    Toggle,
    Lock,
    Unlock,
}

/// <summary>
/// Ensures one BlindsDown per Windows session (Local\ mutex) and lets a second launch
/// (e.g. "BlindsDown.exe --toggle") forward a command to the running one via named events.
/// Local\ objects are per session and created with the user's default security, so only this user's
/// processes in this session can see them.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string DefaultNamePrefix = "BlindsDown-6c2a3f9e";

    private readonly Mutex _mutex;
    private readonly Dictionary<InstanceCommand, EventWaitHandle> _events;
    private readonly List<RegisteredWaitHandle> _waits = [];
    private readonly object _gate = new();
    private bool _listening;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex, Dictionary<InstanceCommand, EventWaitHandle> events)
    {
        _mutex = mutex;
        _events = events;
    }

    /// <summary>Raised on a thread-pool thread when another launch forwarded a command.</summary>
    public event Action<InstanceCommand>? CommandReceived;

    /// <summary>Returns the instance if this is the first BlindsDown in the session, otherwise null.</summary>
    public static SingleInstance? TryAcquire() => TryAcquire(DefaultNamePrefix);

    /// <summary>Forwards a command to the running instance. Returns false if none is running.</summary>
    public static bool SendToRunningInstance(InstanceCommand command) => SendToRunningInstance(DefaultNamePrefix, command);

    /// <param name="namePrefix">Kernel object name prefix; tests pass a unique one so they never touch a
    /// real running BlindsDown.</param>
    internal static SingleInstance? TryAcquire(string namePrefix)
    {
        Mutex? mutex = null;
        var events = new Dictionary<InstanceCommand, EventWaitHandle>();
        try
        {
            // Existence, not ownership, is the signal: while this process holds a handle, every later launch
            // sees "already exists". Not owning the mutex avoids abandoned-mutex states when a thread exits.
            mutex = new Mutex(initiallyOwned: false, MutexName(namePrefix), out bool createdNew);
            if (!createdNew)
            {
                return null;
            }

            // Created now (not in Listen) so a command sent during startup waits in its auto-reset event.
            foreach (InstanceCommand command in Enum.GetValues<InstanceCommand>())
            {
                events[command] = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(namePrefix, command));
            }

            var instance = new SingleInstance(mutex, events);
            mutex = null;
            return instance;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Someone else owns an object with our name. Treat it as "already running" rather than crash.
            Log.Warn($"Single-instance check failed: {ex.GetType().Name}: {ex.Message}");
            foreach (EventWaitHandle handle in events.Values)
            {
                handle.Dispose();
            }

            return null;
        }
        finally
        {
            // Still set unless ownership passed to the new instance.
            mutex?.Dispose();
        }
    }

    internal static bool SendToRunningInstance(string namePrefix, InstanceCommand command)
    {
        // The running instance creates its events just after its mutex; retry briefly if a launch lands
        // in that gap (or while it is still starting) instead of reporting "not running".
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName(namePrefix, command), out EventWaitHandle? handle))
                {
                    using (handle)
                    {
                        return handle.Set();
                    }
                }

                if (!Mutex.TryOpenExisting(MutexName(namePrefix), out Mutex? mutex))
                {
                    return false;
                }

                mutex.Dispose();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                Log.Warn($"Could not forward {command} to the running instance: {ex.Message}");
                return false;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>Begins listening for forwarded commands.</summary>
    public void Listen()
    {
        lock (_gate)
        {
            if (_listening || _disposed)
            {
                return;
            }

            _listening = true;
            foreach ((InstanceCommand command, EventWaitHandle handle) in _events)
            {
                _waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, OnSignaled, command, Timeout.Infinite, executeOnlyOnce: false));
            }
        }
    }

    public void Dispose()
    {
        List<RegisteredWaitHandle> waits;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waits = [.. _waits];
            _waits.Clear();
        }

        foreach (RegisteredWaitHandle wait in waits)
        {
            wait.Unregister(null);
        }

        foreach (EventWaitHandle handle in _events.Values)
        {
            handle.Dispose();
        }

        _mutex.Dispose();
    }

    private static string MutexName(string namePrefix) => @"Local\" + namePrefix + "-instance";

    private static string EventName(string namePrefix, InstanceCommand command) =>
        @"Local\" + namePrefix + "-" + command.ToString().ToLowerInvariant();

    private void OnSignaled(object? state, bool timedOut)
    {
        if (_disposed || state is not InstanceCommand command)
        {
            return;
        }

        try
        {
            CommandReceived?.Invoke(command);
        }
        catch (Exception ex)
        {
            // An exception escaping a thread-pool callback would end the process.
            Log.Error($"Handling the forwarded {command} command failed", ex);
        }
    }
}
