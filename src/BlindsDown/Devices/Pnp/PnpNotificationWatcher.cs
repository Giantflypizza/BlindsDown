using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Devices.Pnp;

/// <summary>
/// [Stage 2 win-pnp] Tells the device lock engine when cameras or audio hardware appear, disappear or change
/// state: CM_Register_Notification for the camera interface classes and the audio endpoint class
/// (CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE), plus CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES for device instance
/// events (enable, disable, start, remove). The native callback only posts <see cref="Changed"/> to the engine
/// thread (debounced ~300 ms) and returns at once; it never calls CfgMgr itself (that can deadlock). Keeps the
/// callback delegate alive; CM_Unregister_Notification in Dispose, never from inside a callback.
///
/// The callback is a static [UnmanagedCallersOnly] method, so there is no delegate the garbage collector could
/// collect. Windows gets a small number as the context, looked up in <see cref="Owners"/>; a callback that
/// arrives for a watcher that is gone finds nothing and does nothing.
///
/// Debouncing: a USB webcam arriving fires dozens of events (interfaces, functions, endpoints). The first event
/// arms a timer; events until it fires are folded in; the timer posts one <see cref="Changed"/>. An event after
/// that arms it again, so a change is never lost: listeners always re-read the devices after the last event.
/// </summary>
internal sealed class PnpNotificationWatcher : IDisposable
{
    private const string Component = "Device watcher";
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Interface classes whose arrival or removal matters: cameras (new and old drivers) and audio
    /// endpoints (they decide which hardware is microphone-only).</summary>
    private static readonly Guid[] InterfaceClasses =
    [
        PnpKeys.VideoCameraInterface,
        PnpKeys.SensorCameraInterface,
        PnpKeys.VideoInterface,
        PnpKeys.AudioCaptureInterface,
        PnpKeys.AudioRenderInterface,
    ];

    private static readonly ConcurrentDictionary<nint, PnpNotificationWatcher> Owners = new();
    private static long _lastContext;

    private readonly EngineThread _engine;
    private readonly TimeSpan _debounce;
    private readonly System.Threading.Timer _debounceTimer;
    private readonly Lock _gate = new();
    private readonly List<nint> _registrations = [];
    private int _pending;
    private int _disposed;
    private bool _started;

    public PnpNotificationWatcher(EngineThread engine)
        : this(engine, DefaultDebounce)
    {
    }

    internal PnpNotificationWatcher(EngineThread engine, TimeSpan debounce)
    {
        _engine = engine;
        _debounce = debounce;
        _debounceTimer = new System.Threading.Timer(_ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Context = (nint)Interlocked.Increment(ref _lastContext);
    }

    /// <summary>Raised on the engine thread.</summary>
    public event Action? Changed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>What Windows passes back to <see cref="OnNotification"/> to name this watcher.</summary>
    internal nint Context { get; }

    /// <summary>Live CfgMgr registrations (for diagnostics and tests).</summary>
    internal int RegistrationCount
    {
        get
        {
            lock (_gate)
            {
                return _registrations.Count;
            }
        }
    }

    /// <summary>Registers for notifications. Runs on the engine thread (a call from another thread is queued
    /// there). If Windows refuses every registration, the caller's periodic Reconcile still catches changes.</summary>
    public void Start()
    {
        if (!_engine.IsCurrentThread)
        {
            _engine.Post(Start);
            return;
        }

        if (IsDisposed || _started)
        {
            return;
        }

        _started = true;
        Owners[Context] = this;
        foreach (Guid interfaceClass in InterfaceClasses)
        {
            Register(CmNotifyFilterTypeDeviceInterface, flags: 0, interfaceClass, $"interface class {interfaceClass:B}");
        }

        Register(CmNotifyFilterTypeDeviceInstance, CmNotifyFilterFlagAllDeviceInstances, Guid.Empty, "device instances");
        if (IsDisposed)
        {
            // Disposed on another thread while registering (see Dispose): Teardown may already be done.
            _ = Owners.TryRemove(Context, out _);
            return;
        }

        Log.Info($"{Component}: watching {RegistrationCount} kinds of device events.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_engine.IsCurrentThread)
        {
            Teardown();
        }
        else
        {
            try
            {
                _ = _engine.Invoke(
                    () =>
                    {
                        Teardown();
                        return true;
                    },
                    TeardownTimeout);
            }
            catch (TimeoutException)
            {
                // The engine thread is stuck or already stopped. Unregistering is allowed on any thread except a
                // notification callback, and Teardown is safe against a Start running elsewhere.
                Log.Warn($"{Component}: the device thread did not respond; unregistering on this thread.");
                Teardown();
            }
        }

        _debounceTimer.Dispose();
        Changed = null;
    }

    /// <summary>What a native notification does: arm the debounce timer. Internal so tests can simulate a burst.</summary>
    internal void OnNativeEvent()
    {
        if (IsDisposed || Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        try
        {
            _ = _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed in the meantime: the change no longer matters.
        }
    }

    /// <summary>
    /// PCM_NOTIFY_CALLBACK. Runs on a Windows thread pool thread while CfgMgr waits for it: it only arms a timer
    /// and returns. It must never throw (an exception can't cross into native code) and never calls CfgMgr.
    /// The return value only matters for query-remove events of device-handle filters, which aren't used.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static uint OnNotification(nint notification, nint context, uint action, nint eventData, uint eventDataSize)
    {
        try
        {
            if (Owners.TryGetValue(context, out PnpNotificationWatcher? owner))
            {
                owner.OnNativeEvent();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Component}: notification callback failed", ex);
        }

        return ErrorSuccess;
    }

    private void OnDebounceElapsed()
    {
        // Cleared BEFORE posting: an event from now on arms the timer again, so it is never folded into a
        // Changed whose handler may already have read the devices.
        _ = Interlocked.Exchange(ref _pending, 0);
        _engine.Post(RaiseChanged);
    }

    private void RaiseChanged()
    {
        if (!IsDisposed)
        {
            Changed?.Invoke();
        }
    }

    private unsafe void Register(uint filterType, uint flags, Guid interfaceClass, string what)
    {
        var filter = new CmNotifyFilter
        {
            Size = CmNotifyFilter.StructSize,
            Flags = flags,
            FilterType = filterType,
            ClassGuid = interfaceClass,
        };
        nint handle = 0;
        uint cr = CM_Register_Notification(&filter, Context, &OnNotification, &handle);
        if (cr != CrSuccess || handle == 0)
        {
            Log.Warn($"{Component}: could not watch {what}: {CfgMgrErrors.Describe(cr)}");
            return;
        }

        bool kept;
        lock (_gate)
        {
            // Dispose may have run on another thread (after a timeout) while this registered.
            kept = !IsDisposed;
            if (kept)
            {
                _registrations.Add(handle);
            }
        }

        if (!kept)
        {
            _ = CM_Unregister_Notification(handle);
        }
    }

    /// <summary>Unregisters everything. CM_Unregister_Notification waits for callbacks that are running; this
    /// watcher's callback never waits for anything, so this can't deadlock. Safe to run twice and on any thread but
    /// a callback.</summary>
    private void Teardown()
    {
        nint[] handles;
        lock (_gate)
        {
            handles = [.. _registrations];
            _registrations.Clear();
        }

        foreach (nint handle in handles)
        {
            uint cr = CM_Unregister_Notification(handle);
            if (cr != CrSuccess)
            {
                Log.Warn($"{Component}: could not stop a device notification: {CfgMgrErrors.Describe(cr)}");
            }
        }

        _ = Owners.TryRemove(Context, out _);
    }
}
