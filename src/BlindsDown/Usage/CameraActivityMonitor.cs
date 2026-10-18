using System.Globalization;
using System.Runtime.InteropServices;
using BlindsDown.Audio;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;
using BlindsDown.Interop.Media;

namespace BlindsDown.Usage;

/// <summary>
/// Live camera use, per app: Media Foundation's sensor activity monitor (MFCreateSensorActivityMonitor in
/// mfsensorgroup.dll, Windows 10 1703+). Its callback lists, per camera (symbolic link), the processes that have it
/// open and whether they are streaming. The callback runs on a Media Foundation thread: it only copies the data into
/// an immutable snapshot and posts <see cref="Changed"/> to the engine thread (once per change, bursts coalesced).
///
/// Each report is taken as the complete picture (a camera missing from it is no longer in use), and when one report
/// lists the same process twice for a camera, "streaming" wins: Chromium saw such contradictory entries, and the
/// streaming one was the true one (crbug.com/325590346).
///
/// <see cref="GetActiveSignals"/> returns one <see cref="UsageSignal"/> (Kind Camera) per streaming process and
/// camera. DeviceId is the camera's PnP instance ID (from its symbolic link via CM_Get_Device_Interface_Property
/// DEVPKEY_Device_InstanceId), so it matches the helper service's camera list; when Windows no longer knows the link,
/// the link itself. DeviceName is the report's name, else the device's (DEVPKEY_NAME), else "Camera". PIDs 0 and 4
/// (Windows itself) and the Camera Frame Server services, which open cameras on behalf of apps, are left out.
///
/// If the API is missing or fails, Start logs it once and the monitor stays empty (the usage registry still reports
/// camera use, more slowly). Start never throws.
/// </summary>
internal sealed class CameraActivityMonitor : IDisposable
{
    public const string SourceName = "camera-sensors";

    /// <summary>Shown when neither the report nor Windows names the camera (the usage registry uses the same word).</summary>
    internal const string FallbackCameraName = "Camera";

    private const string Component = "Camera activity";

    /// <summary>Resolved symbolic links kept; a PC has a handful of cameras, so this only bounds a pathological case.</summary>
    private const int MaxCachedDevices = 64;

    private readonly EngineThread _engine;
    private readonly IProcessIdentityResolver _identities;
    private readonly Func<string, CameraDeviceInfo> _resolveDevice;
    private readonly Func<IReadOnlySet<int>> _frameServerProcessIds;
    private readonly ProblemLog _problems = new(Component);
    private readonly object _gate = new();

    /// <summary>Kept alive here for as long as Media Foundation may call it (the monitor also holds a reference).</summary>
    private readonly SensorActivitiesSink _sink;

    /// <summary>Symbolic link -> device; engine thread only. Only successful lookups are cached.</summary>
    private readonly Dictionary<string, CameraDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<CameraUse> _snapshot = [];
    private ComRef<IMFSensorActivityMonitor>? _monitor;
    private bool _mediaFoundationStarted;
    private bool _started;
    private int _changedQueued;
    private int _unreadableReportLogged;
    private int _callbackFailureLogged;
    private int _reportCount;
    private volatile bool _disposed;

    public CameraActivityMonitor(EngineThread engine, IProcessIdentityResolver identities)
        : this(engine, identities, CameraDeviceLookup.Resolve, CameraDeviceLookup.FrameServerProcessIds)
    {
    }

    /// <param name="resolveDevice">Symbolic link -> device (tests pass a fake; the app asks CfgMgr32).</param>
    /// <param name="frameServerProcessIds">The running Camera Frame Server processes.</param>
    internal CameraActivityMonitor(
        EngineThread engine,
        IProcessIdentityResolver identities,
        Func<string, CameraDeviceInfo> resolveDevice,
        Func<IReadOnlySet<int>> frameServerProcessIds)
    {
        _engine = engine;
        _identities = identities;
        _resolveDevice = resolveDevice;
        _frameServerProcessIds = frameServerProcessIds;
        _sink = new SensorActivitiesSink(OnReport, OnCallbackFailure);
    }

    /// <summary>Raised on the engine thread when the set of streaming apps and cameras changed.</summary>
    public event Action? Changed;

    /// <summary>False when the sensor activity API isn't available on this Windows (and until <see cref="Start"/> ran).</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Reports received since construction, readable or not (for diagnostics).</summary>
    internal int ReportCount => Volatile.Read(ref _reportCount);

    /// <summary>Starts the monitor on the engine thread (a call from another thread is queued there). Never throws.</summary>
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
        try
        {
            IsAvailable = StartMonitor();
        }
        catch (Exception ex)
        {
            // Typically DllNotFoundException (Windows without Media Foundation) or EntryPointNotFoundException
            // (Windows older than 1703).
            Log.Warn($"{Component}: the camera activity monitor isn't available on this Windows ({ex.GetType().Name}: {ex.Message}). Camera use is only seen through Windows' usage registry.");
            StopMonitor();
        }
    }

    /// <summary>
    /// The apps streaming from a camera right now, from the latest report. Call on the engine thread. Empty until
    /// <see cref="Start"/> ran, when the API isn't available, and after <see cref="Dispose"/>.
    /// </summary>
    public IReadOnlyList<UsageSignal> GetActiveSignals()
    {
        IReadOnlyList<CameraUse> uses;
        lock (_gate)
        {
            uses = _snapshot;
        }

        if (uses.Count == 0 || _disposed)
        {
            return [];
        }

        try
        {
            IReadOnlyList<UsageSignal> signals = BuildSignals(uses, ResolveDevice, _identities.Resolve, _frameServerProcessIds());
            _problems.Resolve("signals");
            return signals;
        }
        catch (Exception ex)
        {
            // Naming apps and cameras is best effort; a failure here must not break the usage tracker.
            _problems.Report("signals", $"Could not name the apps using a camera: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EngineDispatch.RunTeardown(_engine, StopMonitor, Component);
    }

    /// <summary>
    /// The streaming (camera, process) pairs of one report, merged: one per camera link (compared case-insensitively)
    /// and process, streaming if any entry for it says so, with the first name the report gives. Windows itself
    /// (PID 0 and 4, and invalid PIDs) and entries without a link are dropped. Sorted, so equal reports compare equal.
    /// </summary>
    internal static IReadOnlyList<CameraUse> Summarize(IEnumerable<CameraActivityEntry> entries)
    {
        var uses = new Dictionary<(string Link, int ProcessId), CameraUse>();
        foreach (CameraActivityEntry entry in entries)
        {
            if (!entry.Streaming || IsWindowsItself(entry.ProcessId) || string.IsNullOrWhiteSpace(entry.SymbolicLink))
            {
                continue;
            }

            string? name = string.IsNullOrWhiteSpace(entry.FriendlyName) ? null : entry.FriendlyName.Trim();
            (string, int) key = (entry.SymbolicLink.Trim().ToUpperInvariant(), entry.ProcessId);
            if (uses.TryGetValue(key, out CameraUse? existing))
            {
                if (existing.ReportedName is null && name is not null)
                {
                    uses[key] = existing with { ReportedName = name };
                }

                continue;
            }

            uses[key] = new CameraUse(entry.SymbolicLink.Trim(), name, entry.ProcessId);
        }

        return [.. uses.OrderBy(u => u.Key.Link, StringComparer.Ordinal).ThenBy(u => u.Key.ProcessId).Select(u => u.Value)];
    }

    /// <summary>
    /// One signal per app and camera device. Two links of the same device (a camera has several interfaces) give one
    /// signal; the frame server processes are left out.
    /// </summary>
    internal static IReadOnlyList<UsageSignal> BuildSignals(
        IReadOnlyList<CameraUse> uses,
        Func<string, CameraDeviceInfo> resolveDevice,
        Func<int, AppIdentity> resolveApp,
        IReadOnlySet<int> frameServerProcessIds)
    {
        var signals = new List<UsageSignal>(uses.Count);
        var seen = new HashSet<(int ProcessId, string DeviceId)>();
        foreach (CameraUse use in uses)
        {
            if (IsWindowsItself(use.ProcessId) || frameServerProcessIds.Contains(use.ProcessId))
            {
                continue;
            }

            CameraDeviceInfo device = resolveDevice(use.SymbolicLink);
            string deviceId = string.IsNullOrWhiteSpace(device.InstanceId) ? use.SymbolicLink : device.InstanceId;
            if (!seen.Add((use.ProcessId, deviceId.ToUpperInvariant())))
            {
                continue;
            }

            string name = use.ReportedName ?? (string.IsNullOrWhiteSpace(device.Name) ? FallbackCameraName : device.Name);
            signals.Add(new UsageSignal(CaptureKind.Camera, resolveApp(use.ProcessId), use.ProcessId, deviceId, name));
        }

        return signals;
    }

    /// <summary>Called on a Media Foundation thread (or by tests) with the entries of a new report.</summary>
    internal void OnReport(IReadOnlyList<CameraActivityEntry>? entries)
    {
        if (_disposed)
        {
            return;
        }

        _ = Interlocked.Increment(ref _reportCount);
        if (entries is null)
        {
            if (Interlocked.Exchange(ref _unreadableReportLogged, 1) == 0)
            {
                // Logged from the engine thread: Media Foundation's thread only copies and queues.
                EngineDispatch.PostFromCallback(_engine, () => Log.Warn($"{Component}: Windows sent a camera activity report that could not be read; keeping the previous state."));
            }

            return;
        }

        IReadOnlyList<CameraUse> uses = Summarize(entries);
        bool changed;
        lock (_gate)
        {
            changed = !_snapshot.SequenceEqual(uses);
            if (changed)
            {
                _snapshot = uses;
            }
        }

        if (changed && Interlocked.Exchange(ref _changedQueued, 1) == 0)
        {
            EngineDispatch.PostFromCallback(_engine, RaiseChanged);
        }
    }

    private static bool IsWindowsItself(int processId) => processId is <= 0 or 4;

    private void OnCallbackFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref _callbackFailureLogged, 1) == 0)
        {
            EngineDispatch.PostFromCallback(_engine, () => Log.Error($"{Component}: reading a camera activity report failed", ex));
        }
    }

    private void RaiseChanged()
    {
        Volatile.Write(ref _changedQueued, 0);
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }

    private CameraDeviceInfo ResolveDevice(string symbolicLink)
    {
        if (_devices.TryGetValue(symbolicLink, out CameraDeviceInfo cached))
        {
            return cached;
        }

        CameraDeviceInfo device = _resolveDevice(symbolicLink);
        if (device.InstanceId is not null)
        {
            if (_devices.Count >= MaxCachedDevices)
            {
                _devices.Clear();
            }

            _devices[symbolicLink] = device;
        }

        return device;
    }

    /// <summary>Engine thread. Returns whether the monitor is running; logs why when it isn't.</summary>
    private bool StartMonitor()
    {
        int hr = SensorNative.MFStartup(SensorNative.MfVersion, SensorNative.MfStartupLite);
        if (hr < 0)
        {
            return Unavailable("Media Foundation could not start", hr);
        }

        _mediaFoundationStarted = true;
        nint callback = Marshal.GetComInterfaceForObject<SensorActivitiesSink, IMFSensorActivitiesReportCallback>(_sink);
        nint monitorPointer;
        try
        {
            hr = SensorNative.MFCreateSensorActivityMonitor(callback, out monitorPointer);
        }
        finally
        {
            _ = Marshal.Release(callback);
        }

        if (hr < 0)
        {
            return Unavailable("Windows could not create the camera activity monitor", hr);
        }

        _monitor = ComRef.TryTake<IMFSensorActivityMonitor>(monitorPointer);
        if (_monitor is null)
        {
            return Unavailable("Windows created a camera activity monitor of an unknown kind", CoreAudioErrors.NoInterface);
        }

        hr = _monitor.Value.Start();
        if (hr < 0)
        {
            return Unavailable("the camera activity monitor did not start", hr);
        }

        Log.Info($"{Component}: watching which apps use a camera (Media Foundation sensor activity monitor).");
        return true;
    }

    private bool Unavailable(string what, int hr)
    {
        Log.Warn(string.Create(
            CultureInfo.InvariantCulture,
            $"{Component}: {what} (0x{hr:X8}). Camera use is only seen through Windows' usage registry."));
        StopMonitor();
        return false;
    }

    /// <summary>
    /// Stops a sensor activity monitor and shuts it down, the order Microsoft's sample uses. Windows' monitor has
    /// IMFShutdown, and an object with IMFShutdown must be shut down before it is released (releasing it alone may
    /// leave what it holds behind). A monitor without IMFShutdown is just stopped. Failures are ignored: this only
    /// runs while stopping.
    /// </summary>
    internal static void StopAndShutDown(IMFSensorActivityMonitor monitor)
    {
        _ = monitor.Stop();
        if (monitor is IMFShutdown shutdown)
        {
            _ = shutdown.Shutdown();
        }
    }

    /// <summary>Engine thread. Stops, shuts down and releases the monitor, then balances MFStartup. Safe to call more than once.</summary>
    private void StopMonitor()
    {
        if (_monitor is { } monitor)
        {
            _monitor = null;
            StopAndShutDown(monitor.Value);
            monitor.Dispose();
        }

        if (_mediaFoundationStarted)
        {
            _mediaFoundationStarted = false;
            _ = SensorNative.MFShutdown();
        }

        lock (_gate)
        {
            _snapshot = [];
        }

        IsAvailable = false;
    }

    /// <summary>An app (process) streaming from a camera (by symbolic link), with the name the report gave the camera.</summary>
    internal sealed record CameraUse(string SymbolicLink, string? ReportedName, int ProcessId);
}
