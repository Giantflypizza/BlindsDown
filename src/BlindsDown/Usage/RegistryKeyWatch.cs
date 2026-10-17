using System.Globalization;
using System.Security;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.SystemApis;
using Microsoft.Win32;

namespace BlindsDown.Usage;

/// <summary>
/// Watches one registry key and its subtree with RegNotifyChangeKeyValue, calling back on a thread-pool
/// thread after each change. The notification is one-shot, so the callback re-arms it BEFORE reporting:
/// a change that lands while listeners re-read the key then signals again instead of being lost.
/// </summary>
internal sealed class RegistryKeyWatch : IDisposable
{
    private const uint NotifyFilter =
        SystemNative.RegNotifyChangeName | SystemNative.RegNotifyChangeLastSet | SystemNative.RegNotifyThreadAgnostic;

    private const int ErrorInvalidHandle = 6;

    private readonly RegistryKey _key;
    private readonly string _description;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Action _onChanged;
    private readonly object _gate = new();
    private RegisteredWaitHandle? _wait;
    private volatile bool _broken;
    private bool _disposed;

    private RegistryKeyWatch(RegistryKey key, string description, Action onChanged)
    {
        _key = key;
        _description = description;
        _onChanged = onChanged;
    }

    /// <summary>True once re-arming failed (typically because the key was deleted). The owner should
    /// dispose it and start a new watch when the key exists again.</summary>
    public bool IsBroken => _broken;

    /// <summary>Starts watching, or returns null when the key does not exist or can't be watched.
    /// <paramref name="onChanged"/> runs on a thread-pool thread and must return quickly.</summary>
    public static RegistryKeyWatch? TryStart(RegistryKey hive, string subKeyPath, Action onChanged)
    {
        string description = hive.Name + @"\" + subKeyPath;
        RegistryKey? key;
        try
        {
            key = hive.OpenSubKey(subKeyPath, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        if (key is null)
        {
            return null;
        }

        var watch = new RegistryKeyWatch(key, description, onChanged);
        int result = watch.Arm();
        if (result != 0)
        {
            Log.Warn(string.Create(CultureInfo.InvariantCulture, $"Can't watch {description} for changes (error {result})."));
            watch.Dispose();
            return null;
        }

        watch._wait = ThreadPool.RegisterWaitForSingleObject(watch._signal, OnSignaled, watch, Timeout.Infinite, executeOnlyOnce: false);
        return watch;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_wait is not null)
        {
            // Wait for a running callback, so it never uses the key after it is closed below.
            using var unregistered = new ManualResetEvent(false);
            if (_wait.Unregister(unregistered))
            {
                unregistered.WaitOne(TimeSpan.FromSeconds(2));
            }
        }

        // Closing the key also cancels the pending notification.
        _key.Dispose();
        _signal.Dispose();
    }

    private static void OnSignaled(object? state, bool timedOut)
    {
        if (state is not RegistryKeyWatch watch)
        {
            return;
        }

        lock (watch._gate)
        {
            if (watch._disposed)
            {
                return;
            }

            int result = watch.Arm();
            if (result != 0 && !watch._broken)
            {
                watch._broken = true;
                Log.Warn(string.Create(CultureInfo.InvariantCulture, $"Stopped watching {watch._description} (error {result}); will retry."));
            }
        }

        try
        {
            watch._onChanged();
        }
        catch (Exception ex)
        {
            // An exception escaping a thread-pool callback would end the process.
            Log.Error("A registry change handler failed", ex);
        }
    }

    private int Arm()
    {
        try
        {
            return SystemQueries.NotifyKeyChange(_key.Handle, NotifyFilter, _signal.SafeWaitHandle);
        }
        catch (ObjectDisposedException)
        {
            return ErrorInvalidHandle;
        }
    }
}
