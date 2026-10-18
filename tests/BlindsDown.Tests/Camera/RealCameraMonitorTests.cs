using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;
using BlindsDown.Interop.Media;
using BlindsDown.Usage;

namespace BlindsDown.Tests.Camera;

/// <summary>
/// READ-ONLY tests against this PC's real Media Foundation: the camera activity monitor starts, reports a consistent
/// picture and stops cleanly. They never open a camera (no camera needs to be in use: an empty picture is fine) and
/// never change a device. Skipped unless BLINDSDOWN_DEVICE_TESTS=1.
/// </summary>
public sealed partial class RealCameraMonitorTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>KSCATEGORY_VIDEO_CAMERA: color cameras.</summary>
    private static readonly Guid KsCategoryVideoCamera = new("e5323777-f976-4f5b-9b55-b94699c46e44");

    /// <summary>KSCATEGORY_SENSOR_CAMERA: infrared (Windows Hello) cameras.</summary>
    private static readonly Guid KsCategorySensorCamera = new("24e552d7-6523-47f7-a647-d3465bf1f5ca");

    /// <summary>KSCATEGORY_CAPTURE: every capture filter, cameras and microphones alike.</summary>
    private static readonly Guid KsCategoryCapture = new("65e8773d-8f56-11d0-a3b9-00a0c9223196");

    /// <summary>Reports follow Start by a moment.</summary>
    private static readonly TimeSpan FirstReportWait = TimeSpan.FromSeconds(2);

    [Fact]
    public void MonitorStartsReportsAConsistentPictureAndStopsCleanly()
    {
        SkipUnlessEnabled();
        var errors = new ConcurrentQueue<Exception>();
        using var engine = new EngineThread("BlindsDown camera device test engine", errors.Enqueue);
        var monitor = new CameraActivityMonitor(engine, new ProcessNameResolver());
        int changes = 0;
        monitor.Changed += () => Interlocked.Increment(ref changes);

        IReadOnlyList<UsageSignal> first;
        IReadOnlyList<UsageSignal> second;
        try
        {
            engine.Invoke(
                () =>
                {
                    monitor.Start();
                    return true;
                },
                Timeout);
            Assert.True(monitor.IsAvailable, "The sensor activity monitor should exist on Windows 10 1703 and later");

            Thread.Sleep(FirstReportWait);
            (first, second) = engine.Invoke(() => (monitor.GetActiveSignals(), monitor.GetActiveSignals()), Timeout);

            output.WriteLine($"Reports received: {monitor.ReportCount}; Changed raised: {Volatile.Read(ref changes)}");
            output.WriteLine($"Camera Frame Server processes: {string.Join(", ", CameraDeviceLookup.FrameServerProcessIds())}");
            foreach (UsageSignal signal in first)
            {
                output.WriteLine($"{signal.App.DisplayName} (PID {signal.ProcessId}) is streaming from \"{signal.DeviceName}\" <{signal.DeviceId}>");
            }

            if (first.Count == 0)
            {
                output.WriteLine("No app is streaming from a camera right now.");
            }
        }
        finally
        {
            monitor.Dispose(); // From the test thread: the teardown must still run on the engine thread.
        }

        Assert.All(first, s =>
        {
            Assert.Equal(CaptureKind.Camera, s.Kind);
            Assert.True(s.ProcessId is > 0 and not 4);
            Assert.False(s.DeviceUnknown);
            Assert.False(string.IsNullOrWhiteSpace(s.DeviceName));
        });
        Assert.Equal(first.Count, first.Select(s => (s.ProcessId, s.DeviceId.ToUpperInvariant())).Distinct().Count());
        Assert.Equal(
            first.Select(s => $"{s.ProcessId}|{s.DeviceId}").Order(StringComparer.Ordinal),
            second.Select(s => $"{s.ProcessId}|{s.DeviceId}").Order(StringComparer.Ordinal));

        Assert.False(monitor.IsAvailable);
        Assert.Empty(engine.Invoke(monitor.GetActiveSignals, Timeout));
        Assert.Empty(errors);
    }

    /// <summary>
    /// The raw reports, read the way the monitor reads them, so a wrong vtable slot in the per-camera interfaces would
    /// show here whenever Windows lists a camera (it may list none while no app has one open).
    /// </summary>
    [Fact]
    public void RawReportsAreReadable()
    {
        SkipUnlessEnabled();
        var reports = new ConcurrentQueue<IReadOnlyList<CameraActivityEntry>?>();
        var failures = new ConcurrentQueue<Exception>();
        var sink = new SensorActivitiesSink(reports.Enqueue, failures.Enqueue);
        var errors = new ConcurrentQueue<Exception>();
        using var engine = new EngineThread("BlindsDown camera device test engine", errors.Enqueue);

        bool started = engine.Invoke(() => RunMonitor(sink), Timeout);

        Assert.True(started);
        output.WriteLine($"Reports: {reports.Count}");
        foreach (IReadOnlyList<CameraActivityEntry>? report in reports)
        {
            output.WriteLine(report is null ? "  (unreadable)" : $"  {report.Count} entries");
            foreach (CameraActivityEntry entry in report ?? [])
            {
                output.WriteLine($"    PID {entry.ProcessId} streaming={entry.Streaming} \"{entry.FriendlyName}\" {entry.SymbolicLink} -> {CameraDeviceLookup.Resolve(entry.SymbolicLink)}");
            }
        }

        Assert.Empty(failures);
        Assert.All(reports, Assert.NotNull);
        Assert.All(reports.SelectMany(r => r!), e => Assert.False(string.IsNullOrWhiteSpace(e.SymbolicLink)));
        Assert.Empty(errors);
    }

    [Fact]
    public void StartingTwiceAndDisposingTwiceIsHarmless()
    {
        SkipUnlessEnabled();
        var errors = new ConcurrentQueue<Exception>();
        using var engine = new EngineThread("BlindsDown camera device test engine", errors.Enqueue);
        var monitor = new CameraActivityMonitor(engine, new ProcessNameResolver());

        monitor.Start(); // From another thread: queued to the engine thread.
        monitor.Start();
        _ = engine.Invoke(() => monitor.IsAvailable, Timeout);
        monitor.Dispose();
        monitor.Dispose();
        monitor.Start(); // After Dispose: ignored.
        _ = engine.Invoke(() => true, Timeout);

        Assert.False(monitor.IsAvailable);
        Assert.Empty(errors);
    }

    [Fact]
    public void LookupsOfUnknownCamerasGiveNothing()
    {
        SkipUnlessEnabled();

        Assert.Equal(default, CameraDeviceLookup.Resolve(@"\\?\USB#VID_0000&PID_0000#nothing#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL"));
        Assert.Equal(default, CameraDeviceLookup.Resolve(string.Empty));
        Assert.All(CameraDeviceLookup.FrameServerProcessIds(), id => Assert.True(id > 4));
    }

    /// <summary>
    /// The lookup behind every signal's DeviceId, on this PC's real device interfaces: each present camera interface,
    /// and each capture-filter interface (USB ones end in a "\GLOBAL" reference string, like camera links do), resolves
    /// to the device whose instance ID its symbolic link spells out, and that device has a name. Read-only: CfgMgr32
    /// lists interfaces and reads two properties.
    /// </summary>
    [Fact]
    public void RealDeviceInterfacesResolveToTheirDevice()
    {
        SkipUnlessEnabled();
        List<string> links = [.. new[] { KsCategoryVideoCamera, KsCategorySensorCamera, KsCategoryCapture }.SelectMany(PresentInterfaces)];

        foreach (string link in links)
        {
            CameraDeviceInfo device = CameraDeviceLookup.Resolve(link);
            output.WriteLine($"{link} -> {device.InstanceId} \"{device.Name}\"");
            Assert.Equal(InstanceIdSpelledBy(link), device.InstanceId, ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        }

        output.WriteLine($"{links.Count(l => !l.EndsWith('}'))} of {links.Count} links end in a reference string.");
        Assert.NotEmpty(links);
    }

    /// <summary>
    /// The instance ID inside a symbolic link: "\\?\USB#VID_046D&amp;PID_085C&amp;MI_00#7&amp;1a2b&amp;0&amp;0000#{class}\GLOBAL"
    /// spells "USB\VID_046D&amp;PID_085C&amp;MI_00\7&amp;1a2b&amp;0&amp;0000".
    /// </summary>
    private static string InstanceIdSpelledBy(string link) =>
        link[4..link.LastIndexOf("#{", StringComparison.Ordinal)].Replace('#', '\\');

    /// <summary>Symbolic links of the present interfaces of one class (empty when there are none or the list changed meanwhile).</summary>
    private static unsafe List<string> PresentInterfaces(Guid category)
    {
        uint length = 0;
        if (InterfaceListNative.CM_Get_Device_Interface_List_SizeW(&length, &category, null, 0) != 0 || length <= 1)
        {
            return [];
        }

        char[] buffer = new char[length];
        fixed (char* text = buffer)
        {
            if (InterfaceListNative.CM_Get_Device_Interface_ListW(&category, null, text, length, 0) != 0)
            {
                return [];
            }
        }

        return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Engine thread: the monitor's own start/stop sequence around <paramref name="sink"/>, for two seconds. True only
    /// when every step worked, including the shutdown through IMFShutdown that the monitor relies on.
    /// </summary>
    private static bool RunMonitor(SensorActivitiesSink sink)
    {
        if (SensorNative.MFStartup(SensorNative.MfVersion, SensorNative.MfStartupLite) < 0)
        {
            return false;
        }

        try
        {
            nint callback = Marshal.GetComInterfaceForObject<SensorActivitiesSink, IMFSensorActivitiesReportCallback>(sink);
            nint pointer;
            try
            {
                if (SensorNative.MFCreateSensorActivityMonitor(callback, out pointer) < 0)
                {
                    return false;
                }
            }
            finally
            {
                _ = Marshal.Release(callback);
            }

            using ComRef<IMFSensorActivityMonitor>? monitor = ComRef.TryTake<IMFSensorActivityMonitor>(pointer);
            if (monitor is null || monitor.Value.Start() < 0)
            {
                return false;
            }

            Thread.Sleep(FirstReportWait);
            int stopped = monitor.Value.Stop();
            int shutDown = monitor.Value is IMFShutdown shutdown ? shutdown.Shutdown() : CoreAudioErrors.NoInterface;
            return stopped >= 0 && shutDown >= 0;
        }
        finally
        {
            _ = SensorNative.MFShutdown();
        }
    }

    private static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Uses this PC's real Media Foundation; set BLINDSDOWN_DEVICE_TESTS=1 to run.");

    /// <summary>Read-only CfgMgr32 listing of device interfaces (CM_GET_DEVICE_INTERFACE_LIST_PRESENT is flag 0).</summary>
    private static unsafe partial class InterfaceListNative
    {
        [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
        public static partial int CM_Get_Device_Interface_List_SizeW(uint* length, Guid* interfaceClass, char* deviceId, uint flags);

        [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
        public static partial int CM_Get_Device_Interface_ListW(Guid* interfaceClass, char* deviceId, char* buffer, uint length, uint flags);
    }

    /// <summary>Stand-in for the real resolver: the process name is enough to see who is streaming.</summary>
    private sealed class ProcessNameResolver : IProcessIdentityResolver
    {
        public AppIdentity Resolve(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return new AppIdentity("pid:" + processId, process.ProcessName, null, null);
            }
            catch (ArgumentException)
            {
                return AppIdentity.Unknown;
            }
            catch (InvalidOperationException)
            {
                return AppIdentity.Unknown;
            }
        }

        public bool IsExeRunning(string exePath) => false;

        public bool IsPackageRunning(string packageFamilyName) => false;
    }
}
