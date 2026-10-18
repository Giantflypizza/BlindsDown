using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Devices.Pnp;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Tests.Pnp;

/// <summary>Enumerate against a scripted device tree: which devices are listed, how, and in what state.</summary>
public sealed class CfgMgrPnpBackendEnumerateTests
{
    private readonly FakeCfgMgr _cfgMgr = new();
    private readonly FakeEndpoints _endpoints = new();

    [Fact]
    public void CamerasAreClassifiedFromTheFiltersTheyRegister()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\1", color: ["GLOBAL"]);
        AddCamera(@"USB\VID_0002&PID_0002&MI_00\2", sensor: ["GLOBAL"]);
        AddCamera(@"USB\VID_0003&PID_0003&MI_00\3", color: ["GLOBAL"], sensor: ["GLOBAL"]);
        AddCamera(@"USB\VID_0004&PID_0004&MI_00\4", color: ["GLOBAL"], sensor: ["IR"]);
        AddCamera(@"SWD\VCAMDEVAPI\5", color: ["GLOBAL"]);

        IReadOnlyList<PnpDevice> devices = Backend().Enumerate();

        Assert.Equal(CameraType.Color, Find(devices, @"USB\VID_0001&PID_0001&MI_00\1").CameraType);
        Assert.Equal(CameraType.Infrared, Find(devices, @"USB\VID_0002&PID_0002&MI_00\2").CameraType);
        Assert.Equal(CameraType.Color, Find(devices, @"USB\VID_0003&PID_0003&MI_00\3").CameraType);
        Assert.Equal(CameraType.ColorAndInfrared, Find(devices, @"USB\VID_0004&PID_0004&MI_00\4").CameraType);
        PnpDevice software = Find(devices, @"SWD\VCAMDEVAPI\5");
        Assert.Equal(CameraType.Virtual, software.CameraType);
        Assert.True(software.IsVirtual);
        Assert.All(devices, d => Assert.Equal(DeviceKind.Camera, d.Kind));
        Assert.Equal(5, devices.Count);
    }

    [Fact]
    public void OlderCamerasAreFoundButScannersAndCaptureCardsAreNot()
    {
        AddNode(@"USB\VID_0005&PID_0005\OLDCAM", "Old webcam", PnpKeys.ImageClass);
        _cfgMgr.AddInterface(PnpKeys.VideoInterface, @"USB\VID_0005&PID_0005\OLDCAM");
        _cfgMgr.AddInterface(PnpKeys.CaptureInterface, @"USB\VID_0005&PID_0005\OLDCAM");

        AddNode(@"USB\VID_0006&PID_0006\CAMCLASS", "Camera class webcam", PnpKeys.CameraClass);
        _cfgMgr.AddInterface(PnpKeys.VideoInterface, @"USB\VID_0006&PID_0006\CAMCLASS");
        _cfgMgr.AddInterface(PnpKeys.CaptureInterface, @"USB\VID_0006&PID_0006\CAMCLASS");

        // Image class without any video filter: a scanner.
        AddNode(@"USB\VID_04A9&PID_1909\SCANNER", "Scanner", PnpKeys.ImageClass);

        // A video filter that doesn't capture (e.g. a video renderer) on an Image class device.
        AddNode(@"USB\VID_0007&PID_0007\NOCAPTURE", "No capture", PnpKeys.ImageClass);
        _cfgMgr.AddInterface(PnpKeys.VideoInterface, @"USB\VID_0007&PID_0007\NOCAPTURE");
        _cfgMgr.AddInterface(PnpKeys.CaptureInterface, @"USB\VID_0007&PID_0007\NOCAPTURE", reference: "OTHER");

        // An HDMI capture card: video capture, but setup class Media.
        AddNode(@"USB\VID_0FD9&PID_0066\CAPTURECARD", "Capture card", new Guid("4D36E96C-E325-11CE-BFC1-08002BE10318"));
        _cfgMgr.AddInterface(PnpKeys.VideoInterface, @"USB\VID_0FD9&PID_0066\CAPTURECARD");
        _cfgMgr.AddInterface(PnpKeys.CaptureInterface, @"USB\VID_0FD9&PID_0066\CAPTURECARD");

        IReadOnlyList<PnpDevice> devices = Backend().Enumerate();

        Assert.Equal([@"USB\VID_0005&PID_0005\OLDCAM", @"USB\VID_0006&PID_0006\CAMCLASS"], devices.Select(d => d.InstanceId).Order(StringComparer.Ordinal));
        Assert.All(devices, d => Assert.Equal(CameraType.Color, d.CameraType));
    }

    [Fact]
    public void AModernCameraIsNotListedTwiceThroughItsOlderInterfaces()
    {
        AddCamera(@"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000", color: ["GLOBAL"]);
        _cfgMgr.AddInterface(PnpKeys.VideoInterface, @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000");
        _cfgMgr.AddInterface(PnpKeys.CaptureInterface, @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000");

        PnpDevice camera = Assert.Single(Backend().Enumerate());

        Assert.Equal(CameraType.Color, camera.CameraType);
    }

    [Fact]
    public void CameraIdsUseTheDevnodesOwnSpelling()
    {
        const string Canonical = @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000";
        AddNode(Canonical, "USB Webcam", PnpKeys.CameraClass);
        _cfgMgr.AddInterface(PnpKeys.VideoCameraInterface, Canonical, reportedId: @"USB\VID_1234&PID_5678&MI_00\7&1a2b3c4d&0&0000");

        Assert.Equal(Canonical, Assert.Single(Backend().Enumerate()).InstanceId);
    }

    [Fact]
    public void MicrophoneOnlyHardwareIsListedAndHardwareWithSpeakersIsNot()
    {
        const string WebcamMic = @"USB\VID_1234&PID_5678&MI_02\7&1A2B3C4D&0&0002";
        const string Headset = @"USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000";
        const string Gone = @"USB\VID_9999&PID_9999\UNINSTALLED";
        AddNode(WebcamMic, "USB Webcam");
        AddNode(Headset, "USB Headset");
        AddNode(@"BTHHFENUM\BTHHFPAUDIO\D&1F2E3D4C&0&97", "Wireless Earbuds");
        AddNode(@"ROOT\MEDIA\0000", "SteelSeries Sonar Virtual Audio Device");
        _endpoints.Capture(WebcamMic).Capture(Headset).Render(Headset)
            .Capture(@"BTHHFENUM\BTHHFPAUDIO\D&1F2E3D4C&0&97").Capture(@"ROOT\MEDIA\0000").Capture(Gone);

        PnpDevice mic = Assert.Single(Backend().Enumerate());

        Assert.Equal(WebcamMic, mic.InstanceId);
        Assert.Equal(DeviceKind.MicrophoneHardware, mic.Kind);
        Assert.Null(mic.CameraType);
        Assert.Equal("USB Webcam", mic.Name);
        Assert.StartsWith("hw|", mic.StableKey, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadsetWhoseMicrophoneIsASeparateUsbFunctionIsNotMicrophoneHardware()
    {
        const string Parent = @"USB\VID_1038&PID_2202\SERIAL";
        AddNode(@"USB\VID_1038&PID_2202&MI_03\8&1&0&0003", "Chat").Parent = Parent;
        AddNode(@"USB\VID_1038&PID_2202&MI_00\8&1&0&0000", "Game").Parent = Parent;
        _endpoints.Capture(@"USB\VID_1038&PID_2202&MI_03\8&1&0&0003").Render(@"USB\VID_1038&PID_2202&MI_00\8&1&0&0000");

        Assert.Empty(Backend().Enumerate());
    }

    [Fact]
    public void ADeviceWithVideoAndAudioIsListedOnceAsACamera()
    {
        const string Both = @"USB\VID_0FD9&PID_0078\CAMLINK";
        AddCamera(Both, color: ["GLOBAL"]);
        _endpoints.Capture(Both);

        PnpDevice device = Assert.Single(Backend().Enumerate());

        Assert.Equal(DeviceKind.Camera, device.Kind);
    }

    [Fact]
    public void StateComesFromTheLiveStatusOrForUnpluggedDevicesFromTheConfigFlags()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\RUNNING", color: ["GLOBAL"]);
        AddCamera(@"USB\VID_0002&PID_0002&MI_00\DISABLED", color: ["GLOBAL"]).MarkDisabled();
        AddCamera(@"USB\VID_0003&PID_0003&MI_00\RESTART", color: ["GLOBAL"]).Status |= DnNeedRestart;
        AddCamera(@"USB\VID_0004&PID_0004&MI_00\PROBLEM14", color: ["GLOBAL"]).Problem = CmProbNeedRestart;
        FakeCfgMgr.FakeNode unpluggedOff = AddCamera(@"USB\VID_0005&PID_0005&MI_00\UNPLUGGED_OFF", color: ["GLOBAL"]);
        unpluggedOff.Present = false;
        unpluggedOff.ConfigFlags = ConfigFlagDisabled;
        AddCamera(@"USB\VID_0006&PID_0006&MI_00\UNPLUGGED", color: ["GLOBAL"]).Present = false;

        IReadOnlyList<PnpDevice> devices = Backend().Enumerate();

        PnpDevice running = Find(devices, @"USB\VID_0001&PID_0001&MI_00\RUNNING");
        Assert.True(running is { IsPresent: true, IsStarted: true, IsDisabled: false, NeedsRestart: false, ProblemCode: 0 });

        PnpDevice disabled = Find(devices, @"USB\VID_0002&PID_0002&MI_00\DISABLED");
        Assert.True(disabled is { IsPresent: true, IsStarted: false, IsDisabled: true, ProblemCode: 22 });

        Assert.True(Find(devices, @"USB\VID_0003&PID_0003&MI_00\RESTART").NeedsRestart);

        // Problem 14 only counts with DN_HAS_PROBLEM, which this device doesn't have.
        Assert.False(Find(devices, @"USB\VID_0004&PID_0004&MI_00\PROBLEM14").NeedsRestart);

        PnpDevice off = Find(devices, @"USB\VID_0005&PID_0005&MI_00\UNPLUGGED_OFF");
        Assert.True(off is { IsPresent: false, IsStarted: false, IsDisabled: true, ProblemCode: 0 });

        PnpDevice unplugged = Find(devices, @"USB\VID_0006&PID_0006&MI_00\UNPLUGGED");
        Assert.True(unplugged is { IsPresent: false, IsStarted: false, IsDisabled: false });
    }

    [Fact]
    public void AStatusThatCantBeReadFallsBackToThePresenceProperty()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\ODD", color: ["GLOBAL"]).StatusError = CrFailure;

        PnpDevice device = Assert.Single(Backend().Enumerate());

        Assert.True(device is { IsPresent: true, IsStarted: false, IsDisabled: false });
    }

    [Fact]
    public void NamesPreferFriendlyThenBusReportedThenDescription()
    {
        FakeCfgMgr.FakeNode friendly = AddCamera(@"USB\VID_0001&PID_0001&MI_00\1", color: ["GLOBAL"]);
        friendly.FriendlyName = "Friendly";
        friendly.BusReportedName = "Bus";
        FakeCfgMgr.FakeNode bus = AddCamera(@"USB\VID_0002&PID_0002&MI_00\2", color: ["GLOBAL"]);
        bus.FriendlyName = null;
        bus.BusReportedName = "Bus";
        FakeCfgMgr.FakeNode nameless = AddCamera(@"USB\VID_0003&PID_0003&MI_00\3", color: ["GLOBAL"]);
        nameless.FriendlyName = null;
        nameless.Description = null;

        IReadOnlyList<PnpDevice> devices = Backend().Enumerate();

        Assert.Equal("Friendly", Find(devices, @"USB\VID_0001&PID_0001&MI_00\1").Name);
        Assert.Equal("Bus", Find(devices, @"USB\VID_0002&PID_0002&MI_00\2").Name);
        Assert.Equal("Camera", Find(devices, @"USB\VID_0003&PID_0003&MI_00\3").Name);
    }

    [Fact]
    public void CamerasComeFirstThenMicrophoneHardwareEachByName()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\1", color: ["GLOBAL"]).FriendlyName = "Zeta cam";
        AddCamera(@"USB\VID_0002&PID_0002&MI_00\2", color: ["GLOBAL"]).FriendlyName = "alpha cam";
        AddNode(@"USB\VID_0003&PID_0003&MI_02\3", "Beta mic");
        AddNode(@"USB\VID_0004&PID_0004\4", "Alpha mic");
        _endpoints.Capture(@"USB\VID_0003&PID_0003&MI_02\3").Capture(@"USB\VID_0004&PID_0004\4");

        Assert.Equal(["alpha cam", "Zeta cam", "Alpha mic", "Beta mic"], Backend().Enumerate().Select(d => d.Name));
    }

    [Fact]
    public void InterfacesAndDevicesWindowsCantResolveAreSkipped()
    {
        _cfgMgr.AddOrphanInterface(PnpKeys.VideoCameraInterface, @"\\?\USB#VID_0009#ORPHAN#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL");
        _cfgMgr.AddInterface(PnpKeys.VideoCameraInterface, @"USB\VID_0010&PID_0010&MI_00\NOT_INSTALLED");
        AddCamera(@"USB\VID_0011&PID_0011&MI_00\OK", color: ["GLOBAL"]);

        Assert.Equal(@"USB\VID_0011&PID_0011&MI_00\OK", Assert.Single(Backend().Enumerate()).InstanceId);
    }

    [Fact]
    public void EnumerationFailsLoudlyWhenWindowsCantListDevices()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\1", color: ["GLOBAL"]);
        _cfgMgr.InterfaceListErrors[PnpKeys.SensorCameraInterface] = CrOutOfMemory;

        var ex = Assert.Throws<DeviceOperationException>(() => Backend().Enumerate());
        Assert.Contains("CR_OUT_OF_MEMORY", ex.Message, StringComparison.Ordinal);

        _cfgMgr.InterfaceListErrors.Clear();
        _endpoints.Fail = true;
        Assert.Throws<DeviceOperationException>(() => Backend().Enumerate());
    }

    [Fact]
    public void EnumerationChangesNothing()
    {
        AddCamera(@"USB\VID_0001&PID_0001&MI_00\1", color: ["GLOBAL"]);
        AddNode(@"USB\VID_0003&PID_0003&MI_02\3", "Mic");
        _endpoints.Capture(@"USB\VID_0003&PID_0003&MI_02\3");

        Assert.Equal(2, Backend().Enumerate().Count);

        Assert.Empty(_cfgMgr.DisableCalls);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    private static PnpDevice Find(IReadOnlyList<PnpDevice> devices, string id) =>
        Assert.Single(devices, d => string.Equals(d.InstanceId, id, StringComparison.Ordinal));

    private CfgMgrPnpBackend Backend() => new(_cfgMgr, _endpoints);

    private FakeCfgMgr.FakeNode AddNode(string id, string name, Guid? setupClass = null)
    {
        FakeCfgMgr.FakeNode node = _cfgMgr.Add(id);
        node.FriendlyName = name;
        node.Description = "Generic device";
        node.SetupClass = setupClass;
        return node;
    }

    private FakeCfgMgr.FakeNode AddCamera(string id, string[]? color = null, string[]? sensor = null)
    {
        FakeCfgMgr.FakeNode node = AddNode(id, "Camera " + id[^1], PnpKeys.CameraClass);
        foreach (string reference in color ?? [])
        {
            _cfgMgr.AddInterface(PnpKeys.VideoCameraInterface, id, reference);
        }

        foreach (string reference in sensor ?? [])
        {
            _cfgMgr.AddInterface(PnpKeys.SensorCameraInterface, id, reference);
        }

        return node;
    }
}
