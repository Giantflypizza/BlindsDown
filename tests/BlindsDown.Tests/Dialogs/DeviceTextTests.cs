using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

public class DeviceTextTests
{
    [Theory]
    [InlineData(DeviceProtection.HardwareMute, "Muted in hardware")]
    [InlineData(DeviceProtection.SoftwareMute, "Muted in software (exclusive-mode apps can bypass)")]
    [InlineData(DeviceProtection.DisabledByUser, "Disabled in Windows – left alone")]
    [InlineData(DeviceProtection.NotPresent, "Not connected – locked when it appears")]
    [InlineData(DeviceProtection.Excluded, "Excluded")]
    [InlineData(DeviceProtection.EndpointDisabled, "Turned off in Windows (apps can't open it)")]
    [InlineData(DeviceProtection.DeviceDisabled, "Turned off (device disabled)")]
    [InlineData(DeviceProtection.NeedsHelper, "Not locked: needs the camera helper")]
    [InlineData(DeviceProtection.None, "–")]
    public void Protection_HasPlainWordsForEveryState(DeviceProtection protection, string expected) =>
        Assert.Equal(expected, DeviceText.Protection(protection));

    [Fact]
    public void Protection_ExplainsAFailedLock() =>
        Assert.Equal("Not locked: Mute did not take effect", DeviceText.Protection(DeviceProtection.None, "Mute did not take effect"));

    [Fact]
    public void EveryProtectionValueIsDescribed()
    {
        foreach (DeviceProtection protection in Enum.GetValues<DeviceProtection>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DeviceText.Protection(protection)));
        }
    }

    [Fact]
    public void Type_IsPhysicalOrVirtual()
    {
        Assert.Equal("Physical", DeviceText.Type(isVirtual: false));
        Assert.Equal("Virtual", DeviceText.Type(isVirtual: true));
    }

    [Theory]
    [InlineData(CameraType.Color, "Colour")]
    [InlineData(CameraType.Infrared, "Infrared")]
    [InlineData(CameraType.ColorAndInfrared, "Colour + infrared")]
    [InlineData(CameraType.Virtual, "Virtual")]
    public void Type_OfACameraSaysWhatItSees(CameraType camera, string expected) =>
        Assert.Equal(expected, DeviceText.Type(Camera("c", camera)));

    [Fact]
    public void Type_OfOtherDevicesIsPhysicalOrVirtual()
    {
        Assert.Equal("Virtual", DeviceText.Type(Device("{v}", "mic|v") with { IsVirtual = true }));
        Assert.Equal("Physical", DeviceText.Type(Hardware("hw1")));

        // A camera whose type the helper couldn't tell still gets a word.
        Assert.Equal("Physical", DeviceText.Type(Camera("c", CameraType.Color) with { Camera = null }));
    }

    [Theory]
    [InlineData(DeviceKind.Microphone, "Microphone")]
    [InlineData(DeviceKind.Camera, "Camera")]
    [InlineData(DeviceKind.MicrophoneHardware, "Mic hardware")]
    public void Kind_HasAShortWord(DeviceKind kind, string expected) => Assert.Equal(expected, DeviceText.Kind(kind));

    [Fact]
    public void Preview_Unlocked_MicrophonesFollowTheStrongerLockOption()
    {
        DeviceLockInfo softwareMic = Device("{a}", "mic|a");
        DeviceLockInfo hardwareMic = softwareMic with { HasHardwareMute = true };
        DeviceLockInfo virtualMic = softwareMic with { IsVirtual = true };

        Assert.Equal("Software mute (exclusive-mode apps can bypass)", Preview(softwareMic, Options()));
        Assert.Equal("Can mute in hardware", Preview(hardwareMic, Options()));
        Assert.Equal("Muted and turned off in Windows while locked", Preview(softwareMic, Options() with { DisableMicsInWindows = true }));

        // Virtual microphones are only ever muted.
        Assert.Equal("Software mute (exclusive-mode apps can bypass)", Preview(virtualMic, Options() with { DisableMicsInWindows = true }));
    }

    [Fact]
    public void Preview_Unlocked_CamerasFollowTheCameraOptions()
    {
        DeviceLockInfo color = Camera("c", CameraType.Color);
        DeviceLockInfo infrared = Camera("ir", CameraType.Infrared);
        DeviceLockInfo both = Camera("both", CameraType.ColorAndInfrared);
        DeviceLockInfo virtualCamera = Camera("v", CameraType.Virtual) with { IsVirtual = true };

        Assert.Equal("Turned off while locked", Preview(color, Options()));
        Assert.Equal("Turned off while locked (Windows Hello too)", Preview(both, Options()));
        Assert.Equal("Left on: camera lock is off", Preview(both, Options() with { LockCameras = false }));
        Assert.Equal("Left on: infrared cameras aren't locked", Preview(infrared, Options()));
        Assert.Equal("Turned off while locked", Preview(infrared, Options() with { IncludeInfrared = true }));
        Assert.Equal("Left on: virtual cameras aren't locked", Preview(virtualCamera, Options()));
        Assert.Equal("Turned off while locked", Preview(virtualCamera, Options() with { IncludeVirtual = true }));
        Assert.Equal("Left on: camera lock is off", Preview(color, Options() with { LockCameras = false }));
    }

    [Fact]
    public void Preview_Unlocked_WithoutTheHelperCamerasAndHardwareNeedIt()
    {
        LockPreview noHelper = Options() with { HelperAvailable = false };

        Assert.Equal("Not locked: needs the camera helper", Preview(Camera("c", CameraType.Color), noHelper));
        Assert.Equal("Left on: needs the camera helper", Preview(Hardware("hw"), noHelper));

        // The integrator may already say so per device, whatever the options.
        Assert.Equal("Not locked: needs the camera helper", Preview(Camera("c", CameraType.Color) with { Protection = DeviceProtection.NeedsHelper }, Options()));
    }

    [Fact]
    public void Preview_Unlocked_MicrophoneHardwareFollowsItsOption()
    {
        Assert.Equal("Left on (its microphones are locked)", Preview(Hardware("hw"), Options()));
        Assert.Equal("Turned off while locked", Preview(Hardware("hw"), Options() with { DisableMicHardware = true }));
    }

    [Fact]
    public void Preview_Unlocked_TheCheckboxWinsOverTheSavedExclusion()
    {
        DeviceLockInfo savedAsExcluded = Camera("c", CameraType.Color) with { Protection = DeviceProtection.Excluded };

        Assert.Equal("Turned off while locked", DeviceText.Protection(savedAsExcluded, locked: false, Options(), excluded: false));
        Assert.Equal("Excluded", DeviceText.Protection(Camera("c", CameraType.Color), locked: false, Options(), excluded: true));
    }

    [Fact]
    public void Preview_Unlocked_StatesThatDontDependOnOptionsAlwaysShow()
    {
        Assert.Equal("Disabled in Windows – left alone", Preview(Camera("c", CameraType.Color) with { Protection = DeviceProtection.DisabledByUser }, Options()));
        Assert.Equal("Not connected – locked when it appears", Preview(Device("{a}", "mic|a") with { Protection = DeviceProtection.NotPresent }, Options()));
        Assert.Equal("Not locked: Windows is busy", Preview(Camera("c", CameraType.Color) with { Problem = "Windows is busy" }, Options()));
    }

    [Fact]
    public void Protection_Locked_ShowsTheRealStateAndWhyADeviceWasLeftOn()
    {
        Assert.Equal("Turned off (device disabled)", Locked(Camera("c", CameraType.Color) with { Protection = DeviceProtection.DeviceDisabled }));
        Assert.Equal("Turned off in Windows (apps can't open it)", Locked(Device("{a}", "mic|a") with { Protection = DeviceProtection.EndpointDisabled }));
        Assert.Equal("Not locked: In use by an app; BlindsDown keeps trying", Locked(Camera("c", CameraType.Color) with { Problem = "In use by an app; BlindsDown keeps trying" }));
        Assert.Equal("Left on: infrared camera", Locked(Camera("ir", CameraType.Infrared)));
        Assert.Equal("Left on: virtual camera", Locked(Camera("v", CameraType.Virtual)));
        Assert.Equal("Left on", Locked(Camera("c", CameraType.Color)));
        Assert.Equal("Left on (its microphones are locked)", Locked(Hardware("hw")));
        Assert.Equal("–", Locked(Device("{a}", "mic|a")));

        // While locked the saved state counts, not the (read-only) checkbox.
        Assert.Equal("Excluded", Locked(Camera("c", CameraType.Color) with { Protection = DeviceProtection.Excluded }));
    }

    [Fact]
    public void LockPreview_ComesFromTheSettings()
    {
        var settings = new AppSettings { LockCameras = false, IncludeInfraredCameras = true, IncludeVirtualCameras = true, DisableMicOnlyHardware = true, DisableMicsInWindows = true };

        Assert.Equal(new LockPreview(true, false, true, true, true, true), LockPreview.From(settings, helperAvailable: true));
    }

    [Theory]
    [InlineData("mic|USB|MIC", DeviceKind.Microphone)]
    [InlineData("CAM|usb|cam", DeviceKind.Camera)]
    [InlineData("hw|USB|BRIO", DeviceKind.MicrophoneHardware)]
    [InlineData("other|x", null)]
    [InlineData("nobar", null)]
    public void Exclusion_KindComesFromTheKeyPrefix(string stableKey, DeviceKind? kind) =>
        Assert.Equal(kind, DeviceExclusions.KindOf(Exclusion(stableKey)));

    [Fact]
    public void Exclusion_OnlyAppliesToItsOwnKind()
    {
        // The same model's camera and microphone functions; ids made identical on purpose to prove the kind check.
        DeviceLockInfo camera = Camera(@"USB\VID_046D&PID_085E&MI_00\1", CameraType.Color);
        DeviceLockInfo mic = Device(@"USB\VID_046D&PID_085E&MI_00\1", "mic|same");
        DeviceExclusion forCamera = new() { StableKey = camera.StableKey, LastKnownId = camera.Id, DisplayName = "Cam" };

        Assert.True(DeviceExclusions.IsExcluded([forCamera], camera, [camera, mic]));
        Assert.False(DeviceExclusions.IsExcluded([forCamera], mic, [camera, mic]));
    }

    [Fact]
    public void Exclusion_TwinsAreCountedPerKind()
    {
        // A camera that moved to another port (new id), and a device of another kind with the very same key (made
        // up: real keys differ by their prefix). Among cameras the moved one is unique, so the exclusion follows it.
        DeviceLockInfo movedCamera = Camera("cam-new-port", CameraType.Color) with { StableKey = "cam|USB\\X|CAM" };
        DeviceLockInfo otherKind = Hardware("hw-1") with { StableKey = "cam|USB\\X|CAM" };
        DeviceExclusion forCamera = new() { StableKey = "cam|USB\\X|CAM", LastKnownId = "cam-old-port", DisplayName = "Cam" };

        Assert.True(DeviceExclusions.IsExcluded([forCamera], movedCamera, [movedCamera, otherKind]));
        Assert.False(DeviceExclusions.IsExcluded([forCamera], otherKind, [movedCamera, otherKind]));
    }

    [Fact]
    public void Exclusion_ForACameraFollowsItAcrossPortsUnlessATwinIsConnected()
    {
        DeviceLockInfo a = Camera("a-new", CameraType.Color) with { StableKey = "cam|same" };
        DeviceLockInfo b = Camera("b", CameraType.Color) with { StableKey = "cam|same" };
        DeviceExclusion forOldA = new() { StableKey = "cam|same", LastKnownId = "a-old", DisplayName = "Cam" };

        Assert.True(DeviceExclusions.IsExcluded([forOldA], a, [a]));
        Assert.False(DeviceExclusions.IsExcluded([forOldA], a, [a, b]));
    }

    [Fact]
    public void Build_RecordsInstanceIdAndStableKeyForEveryKind()
    {
        DeviceLockInfo camera = Camera(@"USB\VID_1\5&1", CameraType.Color);
        DeviceLockInfo hardware = Hardware(@"USB\VID_2\6&2");
        DeviceLockInfo mic = Device("{a}", "mic|a");

        List<DeviceExclusion> result = DeviceExclusions.Build([mic, camera, hardware], []);

        Assert.Equal(
            [
                new DeviceExclusion { StableKey = "mic|a", LastKnownId = "{a}", DisplayName = "Mic" },
                new DeviceExclusion { StableKey = camera.StableKey, LastKnownId = camera.Id, DisplayName = camera.Name },
                new DeviceExclusion { StableKey = hardware.StableKey, LastKnownId = hardware.Id, DisplayName = hardware.Name },
            ],
            result);
    }

    [Fact]
    public void Orphans_AreFoundPerKind()
    {
        DeviceLockInfo mic = Device(@"USB\VID_9\1", "mic|USB\\VID_9|MIC");
        DeviceExclusion cameraGone = new() { StableKey = "cam|USB\\VID_9|MIC", LastKnownId = @"USB\VID_9\1", DisplayName = "Gone camera" };

        // Same id and key body as the microphone, but a camera: it doesn't match the microphone, so it stays an orphan.
        Assert.Equal([cameraGone], DeviceExclusions.Orphans([cameraGone], [mic]));
    }

    [Fact]
    public void Exclusion_MatchesByStableKeyOrLastKnownId()
    {
        DeviceLockInfo device = Device("{id-1}", @"mic|USB\VID_1234&PID_5678&MI_00|MICROPHONE");

        Assert.True(DeviceExclusions.Matches(Exclusion(@"MIC|usb\vid_1234&pid_5678&mi_00|microphone"), device, [device]));
        Assert.True(DeviceExclusions.Matches(Exclusion("mic|other", lastId: "{ID-1}"), device, [device]));
        Assert.False(DeviceExclusions.Matches(Exclusion("mic|other", lastId: "{id-2}"), device, [device]));
        Assert.False(DeviceExclusions.Matches(Exclusion("mic|other"), device, [device]));
    }

    [Fact]
    public void Exclusion_OfOneTwinDoesNotMatchTheOther()
    {
        DeviceLockInfo a = Device("{a}", "mic|same");
        DeviceLockInfo b = Device("{b}", "mic|same");
        DeviceExclusion forA = Exclusion("mic|same", lastId: "{a}");

        Assert.True(DeviceExclusions.IsExcluded([forA], a, [a, b]));
        Assert.False(DeviceExclusions.IsExcluded([forA], b, [a, b]));
    }

    [Fact]
    public void Orphans_AreExclusionsWithoutAConnectedDevice()
    {
        DeviceLockInfo connected = Device("{a}", "mic|a");
        DeviceExclusion forConnected = Exclusion("mic|a");
        DeviceExclusion forUnplugged = Exclusion("mic|webcam");

        List<DeviceExclusion> orphans = DeviceExclusions.Orphans([forConnected, forUnplugged], [connected]);

        Assert.Equal([forUnplugged], orphans);
    }

    [Fact]
    public void Build_RecordsStableKeyAndIdAndKeepsOrphans()
    {
        DeviceLockInfo device = Device("{a}", "mic|a", "Microphone (A)");
        DeviceExclusion orphan = Exclusion("mic|webcam");

        List<DeviceExclusion> result = DeviceExclusions.Build([device, device], [orphan]);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DeviceExclusion { StableKey = "mic|a", LastKnownId = "{a}", DisplayName = "Microphone (A)" }, result[0]);
        Assert.Equal(orphan, result[1]);
    }

    private static DeviceLockInfo Device(string id, string stableKey, string name = "Mic") =>
        new(id, name, stableKey, DeviceKind.Microphone, false, false, DeviceProtection.None, null);

    private static DeviceLockInfo Camera(string id, CameraType type) =>
        new(id, "Camera " + id, "cam|" + id.ToUpperInvariant() + "|CAMERA", DeviceKind.Camera, type == CameraType.Virtual, false, DeviceProtection.None, null, type);

    private static DeviceLockInfo Hardware(string id) =>
        new(id, "Webcam audio " + id, "hw|" + id.ToUpperInvariant() + "|WEBCAM AUDIO", DeviceKind.MicrophoneHardware, false, false, DeviceProtection.None, null);

    /// <summary>Everything at its default: helper installed, cameras locked, infrared/virtual/hardware/stronger lock off.</summary>
    private static LockPreview Options() => new(HelperAvailable: true, LockCameras: true, IncludeInfrared: false, IncludeVirtual: false, DisableMicHardware: false, DisableMicsInWindows: false);

    private static string Preview(DeviceLockInfo device, LockPreview preview) =>
        DeviceText.Protection(device, locked: false, preview, excluded: false);

    private static string Locked(DeviceLockInfo device) =>
        DeviceText.Protection(device, locked: true, Options(), excluded: false);

    private static DeviceExclusion Exclusion(string stableKey, string? lastId = null) =>
        new() { StableKey = stableKey, LastKnownId = lastId, DisplayName = "Mic" };
}
