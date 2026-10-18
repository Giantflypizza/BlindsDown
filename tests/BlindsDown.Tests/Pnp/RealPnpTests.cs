using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Devices.Pnp;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Tests.Pnp;

/// <summary>
/// READ-ONLY tests against this PC's real Plug and Play devices: they list cameras and audio hardware, read
/// properties and status, and register/unregister device notifications. They NEVER disable or enable anything
/// (Disable and Enable are tested with a fake in <see cref="CfgMgrPnpBackendChangeTests"/>).
/// Skipped unless BLINDSDOWN_DEVICE_TESTS=1.
/// </summary>
public sealed partial class RealPnpTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void EnumeratesCamerasAndMicrophoneHardwareWithConsistentDetails()
    {
        SkipUnlessEnabled();

        IReadOnlyList<PnpDevice> devices = new CfgMgrPnpBackend().Enumerate();

        foreach (PnpDevice device in devices)
        {
            output.WriteLine(
                $"{device.Kind,-18} {device.CameraType?.ToString() ?? "-",-16} present={device.IsPresent,-5} started={device.IsStarted,-5} " +
                $"disabled={device.IsDisabled,-5} restart={device.NeedsRestart,-5} problem={device.ProblemCode,-2} virtual={device.IsVirtual,-5} " +
                $"\"{device.Name}\" <{device.InstanceId}>");
        }

        output.WriteLine($"{devices.Count} device(s).");
        Assert.Equal(devices.Count, devices.Select(d => d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(devices, d => Assert.True(PnpDeviceRules.IsValidInstanceId(d.InstanceId), d.InstanceId));
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.All(devices, d => Assert.True(d.Kind is DeviceKind.Camera or DeviceKind.MicrophoneHardware));
        Assert.All(devices.Where(d => d.Kind == DeviceKind.Camera), d => Assert.NotNull(d.CameraType));
        Assert.All(devices.Where(d => d.Kind == DeviceKind.MicrophoneHardware), d => Assert.Null(d.CameraType));
        Assert.All(devices, d => Assert.Equal(d.IsVirtual, d.CameraType == CameraType.Virtual || DeviceIds.IsVirtualHardware(d.InstanceId)));
        Assert.All(devices.Where(d => !d.IsPresent), d => Assert.False(d.IsStarted));
        Assert.All(devices.Where(d => d.IsDisabled && d.IsPresent), d => Assert.Equal((int)CmProbDisabled, d.ProblemCode));

        // Microphone hardware is never Bluetooth or software.
        Assert.All(devices.Where(d => d.Kind == DeviceKind.MicrophoneHardware), d => Assert.False(PnpDeviceRules.IsBluetooth(d.InstanceId) || d.IsVirtual));

        // A webcam known to be a USB composite device (camera plus microphone), named only on the test PC, e.g.
        // BLINDSDOWN_KNOWN_USB_WEBCAM=VID_1234&PID_5678: its camera must be the MI_xx function, never the composite
        // parent (turning that off would take the webcam's microphone too).
        if (Environment.GetEnvironmentVariable("BLINDSDOWN_KNOWN_USB_WEBCAM") is { Length: > 0 } known)
        {
            PnpDevice[] webcams = [.. devices.Where(d => d.Kind == DeviceKind.Camera && d.InstanceId.Contains(known, StringComparison.OrdinalIgnoreCase))];
            Assert.NotEmpty(webcams);
            Assert.All(webcams, d => Assert.True(PnpDeviceRules.IsUsbFunction(d.InstanceId), d.InstanceId));
        }
    }

    [Fact]
    public void MicrophoneHardwareIdsAreTheHardwareIdsCoreAudioReports()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        IReadOnlyList<PnpDevice> devices = new CfgMgrPnpBackend().Enumerate();
        IReadOnlyList<MicEndpoint> mics = engine.Thread.Invoke(
            () =>
            {
                using var backend = new CoreAudioMicBackend();
                return backend.EnumerateMics();
            },
            Timeout);

        // The registry the helper reads and Core Audio describe the same endpoints with the same hardware IDs.
        var coreAudioHardware = mics.Select(m => m.HardwareInstanceId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registryCaptureHardware = new MMDevicesRegistrySource().Read().Where(e => e.IsCapture).Select(e => e.HardwareInstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in coreAudioHardware.Order(StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"Core Audio microphone hardware: {id} (in registry: {registryCaptureHardware.Contains(id)})");
        }

        Assert.Subset(registryCaptureHardware, coreAudioHardware);

        foreach (PnpDevice hardware in devices.Where(d => d.Kind == DeviceKind.MicrophoneHardware))
        {
            IEnumerable<MicEndpoint> behind = mics.Where(m => string.Equals(m.HardwareInstanceId, hardware.InstanceId, StringComparison.Ordinal));
            output.WriteLine($"Microphone hardware \"{hardware.Name}\" <{hardware.InstanceId}> carries: {string.Join(", ", behind.Select(m => $"{m.Name} [{m.State}]"))}");
            Assert.NotEmpty(behind);
        }

        engine.AssertNoErrors();
    }

    [Fact]
    public void ReadsInterfaceListsAndNamesTheirDevices()
    {
        SkipUnlessEnabled();
        var api = new CfgMgrApi();

        foreach ((string name, Guid interfaceClass) in new[]
        {
            ("KSCATEGORY_VIDEO_CAMERA", PnpKeys.VideoCameraInterface),
            ("KSCATEGORY_SENSOR_CAMERA", PnpKeys.SensorCameraInterface),
            ("KSCATEGORY_VIDEO", PnpKeys.VideoInterface),
            ("DEVINTERFACE_AUDIO_CAPTURE", PnpKeys.AudioCaptureInterface),
        })
        {
            Assert.Equal(CrSuccess, api.GetDeviceInterfaces(interfaceClass, out IReadOnlyList<string> paths));
            output.WriteLine($"{name}: {paths.Count} interface(s)");
            foreach (string path in paths)
            {
                string? id = api.GetInterfaceInstanceId(path);
                output.WriteLine($"  {path} -> {id ?? "(unknown)"}");
                Assert.True(PnpDeviceRules.IsValidInstanceId(id), path);
                Assert.Equal(CrSuccess, api.LocateDevNode(id!, includeNotPresent: true, out _));
            }
        }

        // Every microphone endpoint exposes DEVINTERFACE_AUDIO_CAPTURE: the watcher relies on it.
        Assert.Equal(CrSuccess, api.GetDeviceInterfaces(PnpKeys.AudioCaptureInterface, out IReadOnlyList<string> captures));
        Assert.Contains(captures, path => path.Contains("MMDEVAPI#{0.0.1.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigRetValuesMatchWhatWindowsReturns()
    {
        SkipUnlessEnabled();
        var api = new CfgMgrApi();

        Assert.Equal(CrNoSuchDevNode, api.LocateDevNode(@"BLINDSDOWN\NOT_A_DEVICE\0", includeNotPresent: true, out _));
        Assert.Equal(CrInvalidDeviceId, api.LocateDevNode(string.Empty, includeNotPresent: true, out _));
        Assert.Equal(CrInvalidDeviceId, api.LocateDevNode("\0", includeNotPresent: true, out _));

        // The root of the device tree exists everywhere and has no bus-reported name.
        Assert.Equal(CrSuccess, api.LocateDevNode(@"HTREE\ROOT\0", includeNotPresent: false, out uint root));
        Assert.Null(api.GetString(root, PnpKeys.BusReportedDeviceDesc));
        Assert.Equal(CrNoSuchValue, RawPropertyResult(root, PnpKeys.BusReportedDeviceDesc));
        Assert.Equal(CrBufferSmall, RawPropertyResult(root, PnpKeys.InstanceId));
        Assert.Equal(@"HTREE\ROOT\0", api.GetString(root, PnpKeys.InstanceId));
        Assert.Equal(CrSuccess, api.GetStatus(root, out uint status, out _));
        Assert.NotEqual(0u, status & DnStarted);
        Assert.Equal(true, api.GetBoolean(root, PnpKeys.IsPresent));

        // CM_MapCrToWin32Err confirms the numbering of the codes BlindsDown can't trigger without changing a device.
        Assert.Equal(5u, MapCrToWin32Err(CrAccessDenied, 0xFFFF));
        Assert.Equal(122u, MapCrToWin32Err(CrBufferSmall, 0xFFFF));
        Assert.Equal(1168u, MapCrToWin32Err(CrNoSuchValue, 0xFFFF));
        Assert.Equal(1168u, MapCrToWin32Err(CrNoSuchDevNode, 0xFFFF));
        Assert.Equal(120u, MapCrToWin32Err(CrCallNotImplemented, 0xFFFF));
        output.WriteLine($"CR_REMOVE_VETOED -> {MapCrToWin32Err(CrRemoveVetoed, 0xFFFF)}, CR_NOT_DISABLEABLE -> {MapCrToWin32Err(CrNotDisableable, 0xFFFF)}, CR_NEED_RESTART -> {MapCrToWin32Err(CrNeedRestart, 0xFFFF)}, CR_DEVICE_NOT_THERE -> {MapCrToWin32Err(CrDeviceNotThere, 0xFFFF)}");
    }

    [Fact]
    public async Task WatcherRegistersAndUnregistersCleanly()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();
        var watcher = new PnpNotificationWatcher(engine.Thread, TimeSpan.FromMilliseconds(50));
        var raised = new SemaphoreSlim(0);
        watcher.Changed += () => raised.Release();

        watcher.Start(); // From the test thread: queued to the engine thread.
        int registered = await engine.Thread.InvokeAsync(() => watcher.RegistrationCount);
        output.WriteLine($"Registered {registered} notification filter(s).");
        Assert.Equal(6, registered);

        // The native callback, called the way Windows calls it: for this watcher it raises Changed; for a context
        // nobody owns it does nothing.
        Assert.Equal(ErrorSuccess, InvokeCallback(watcher.Context));
        Assert.True(await raised.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorSuccess, InvokeCallback(-1));

        watcher.Dispose(); // From the test thread: the unregistration still runs on the engine thread.
        Assert.Equal(0, watcher.RegistrationCount);
        int raisedBeforeDispose = raised.CurrentCount; // Real device events may have arrived meanwhile.
        Assert.Equal(ErrorSuccess, InvokeCallback(watcher.Context)); // A late callback for a disposed watcher.
        watcher.Dispose();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await engine.Thread.InvokeAsync(() => { });
        Assert.Equal(raisedBeforeDispose, raised.CurrentCount);

        // A second watcher, disposed ON the engine thread.
        var second = new PnpNotificationWatcher(engine.Thread);
        await engine.Thread.InvokeAsync(() =>
        {
            second.Start();
            Assert.Equal(6, second.RegistrationCount);
            second.Dispose();
            Assert.Equal(0, second.RegistrationCount);
        });

        engine.AssertNoErrors();
    }

    private static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Reads this PC's real cameras and audio hardware; set BLINDSDOWN_DEVICE_TESTS=1 to run.");

    private static unsafe uint InvokeCallback(nint context)
    {
        delegate* unmanaged[Stdcall]<nint, nint, uint, nint, uint, uint> callback = &PnpNotificationWatcher.OnNotification;
        return callback(0, context, 0, 0, 0);
    }

    /// <summary>The raw CONFIGRET of a property read into a 2-byte buffer.</summary>
    private static unsafe uint RawPropertyResult(uint devInst, DevPropKey key)
    {
        byte* buffer = stackalloc byte[2];
        uint size = 2;
        uint type;
        return CM_Get_DevNode_PropertyW(devInst, &key, &type, buffer, &size, 0);
    }

    /// <summary>Test-only: maps a CONFIGRET to its Win32 error, to check the CR_* numbering.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_MapCrToWin32Err")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint MapCrToWin32Err(uint configRet, uint defaultError);

    /// <summary>An engine thread like the app's, collecting anything that escapes to its error handler.</summary>
    private sealed class TestEngine : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _errors = new();

        public TestEngine()
        {
            Thread = new EngineThread("BlindsDown PnP test engine", _errors.Enqueue);
        }

        public EngineThread Thread { get; }

        public void AssertNoErrors() => Assert.Empty(_errors);

        public void Dispose() => Thread.Dispose();
    }
}
