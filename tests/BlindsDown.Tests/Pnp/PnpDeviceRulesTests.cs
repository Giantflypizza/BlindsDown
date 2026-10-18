using BlindsDown.Core.Locking;
using BlindsDown.Devices.Pnp;

namespace BlindsDown.Tests.Pnp;

public sealed class PnpDeviceRulesTests
{
    private const string WebcamVideo = @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000";
    private const string WebcamMic = @"USB\VID_1234&PID_5678&MI_02\7&1A2B3C4D&0&0002";
    private const string Headset = @"USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000";

    [Theory]
    [InlineData(WebcamVideo, true)]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256\4&1A2B3C4&0&0001", true)]
    [InlineData(@"SWD\VCAMDEVAPI\0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(@" USB\VID_1\2", false)]
    [InlineData(@"USB\VID_1\2 ", false)]
    [InlineData("NOSLASH", false)]
    [InlineData(@"\LEADING\SLASH", false)]
    public void InstanceIdsAreCheckedBeforeTheyReachWindows(string? id, bool valid) =>
        Assert.Equal(valid, PnpDeviceRules.IsValidInstanceId(id));

    [Fact]
    public void InstanceIdsMustBeShorterThanMaxDeviceIdLengthAndHaveNoNull()
    {
        Assert.True(PnpDeviceRules.IsValidInstanceId(@"USB\" + new string('A', 195)));
        Assert.False(PnpDeviceRules.IsValidInstanceId(@"USB\" + new string('A', 196)));
        Assert.False(PnpDeviceRules.IsValidInstanceId("USB\\VID_1\0\\2"));
    }

    [Theory]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2014\C&1A2B3C4D&0&000000000001_C00000000", true)]
    [InlineData(@"BTHHFENUM\BTHHFPAUDIO\D&1F2E3D4C&0&97", true)]
    [InlineData(@"BTHLEDEVICE\{0000110B-0000-1000-8000-00805F9B34FB}_DEV_VID&01004C\8&1&0", true)]
    [InlineData(@"bthenum\lower\0", true)]
    [InlineData(Headset, false)]
    [InlineData(@"ROOT\MEDIA\0000", false)]
    [InlineData(null, false)]
    public void BluetoothIsRecognisedByItsEnumerator(string? id, bool bluetooth) =>
        Assert.Equal(bluetooth, PnpDeviceRules.IsBluetooth(id));

    [Theory]
    [InlineData(WebcamMic, true)]
    [InlineData(@"usb\vid_1234&pid_5678&mi_02\7&1a2b3c4d&0&0002", true)]
    [InlineData(@"USB\VID_1234&PID_5678\00000001", false)]
    [InlineData(@"USB\VID_1234&PID_5678\MI_00", false)]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&MI_00\1", false)]
    [InlineData(null, false)]
    public void UsbFunctionsAreTheMiPartsOfCompositeDevices(string? id, bool function) =>
        Assert.Equal(function, PnpDeviceRules.IsUsbFunction(id));

    [Fact]
    public void FilterKeyDropsOnlyTheInterfaceClass()
    {
        const string Color = @"\\?\USB#VID_1234&PID_5678&MI_00#7&1a2b3c4d&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
        const string Sensor = @"\\?\usb#vid_1234&pid_5678&mi_00#7&1a2b3c4d&0&0000#{24e552d7-6523-47f7-a647-d3465bf1f5ca}\global";
        const string Infrared = @"\\?\USB#VID_1234&PID_5678&MI_00#7&1a2b3c4d&0&0000#{24e552d7-6523-47f7-a647-d3465bf1f5ca}\IR";

        Assert.Equal(@"USB#VID_1234&PID_5678&MI_00#7&1A2B3C4D&0&0000\GLOBAL", PnpDeviceRules.FilterKey(Color));
        Assert.Equal(PnpDeviceRules.FilterKey(Color), PnpDeviceRules.FilterKey(Sensor));
        Assert.NotEqual(PnpDeviceRules.FilterKey(Color), PnpDeviceRules.FilterKey(Infrared));
    }

    [Fact]
    public void FilterKeyKeepsBracesThatBelongToTheDeviceId()
    {
        const string Endpoint = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{a1b2c3d4-0000-4000-8000-00000000000a}#{2eef81be-33fa-4800-9670-1cd474972c3f}";

        Assert.Equal("SWD#MMDEVAPI#{0.0.1.00000000}.{A1B2C3D4-0000-4000-8000-00000000000A}", PnpDeviceRules.FilterKey(Endpoint));
        Assert.Equal("NO#CLASS#HERE", PnpDeviceRules.FilterKey("no#class#here"));
    }

    [Fact]
    public void CamerasAreClassifiedFromTheirFilters()
    {
        HashSet<string> none = [];
        HashSet<string> global = ["GLOBAL"];
        HashSet<string> infrared = ["IR"];
        HashSet<string> both = ["GLOBAL", "IR"];

        Assert.Equal(CameraType.Color, PnpDeviceRules.ClassifyCamera(WebcamVideo, global, none));
        Assert.Equal(CameraType.Color, PnpDeviceRules.ClassifyCamera(WebcamVideo, global, global));
        Assert.Equal(CameraType.Infrared, PnpDeviceRules.ClassifyCamera(WebcamVideo, none, infrared));
        Assert.Equal(CameraType.ColorAndInfrared, PnpDeviceRules.ClassifyCamera(WebcamVideo, global, both));
        Assert.Equal(CameraType.ColorAndInfrared, PnpDeviceRules.ClassifyCamera(WebcamVideo, global, infrared));

        // Nothing to go on: colour, which is locked by default.
        Assert.Equal(CameraType.Color, PnpDeviceRules.ClassifyCamera(WebcamVideo, none, none));
    }

    [Theory]
    [InlineData(@"SWD\VCAMDEVAPI\01234567")]
    [InlineData(@"ROOT\IMAGE\0000")]
    [InlineData(@"SW\{96E080C7-143C-11D1-B40F-00A0C9223196}\{3C0D501A-140B-11D1-B40F-00A0C9223196}")]
    public void SoftwareCamerasAreVirtualWhateverTheyRegister(string id)
    {
        HashSet<string> global = ["GLOBAL"];
        HashSet<string> infrared = ["IR"];

        Assert.Equal(CameraType.Virtual, PnpDeviceRules.ClassifyCamera(id, global, infrared));
        Assert.Equal(CameraType.Virtual, PnpDeviceRules.ClassifyCamera(id, new HashSet<string>(), infrared));
    }

    [Theory]
    [InlineData("USB Webcam", "Webcam Bus Name", "USB Video Device", "USB Webcam")]
    [InlineData(null, "Realtek USB2.0 Audio", "Realtek USB Audio", "Realtek USB2.0 Audio")]
    [InlineData("  ", " Bus Name ", "Desc", "Bus Name")]
    [InlineData(null, null, "USB Video Device", "USB Video Device")]
    [InlineData(null, null, "@usbvideo.inf,%usbvideo.devicedesc%;USB Video Device", "USB Video Device")]
    [InlineData(null, null, "@usbvideo.inf,%usbvideo.devicedesc%", "Camera")]
    [InlineData(null, null, null, "Camera")]
    public void NamesPreferTheMostSpecificText(string? friendly, string? bus, string? description, string expected) =>
        Assert.Equal(expected, PnpDeviceRules.ChooseName(friendly, bus, description, "Camera"));

    [Fact]
    public void MicrophoneOnlyHardwareHasCaptureAndNoRender()
    {
        AudioEndpointHardware[] endpoints =
        [
            Capture(WebcamMic),
            Capture(WebcamMic), // A second microphone endpoint of the same hardware.
            Capture(Headset),
            Render(Headset),
            Capture(@"USB\VID_1234&PID_9ABC&MI_00\9&5A6B7C8D&0&0000"),
            Render(@"usb\vid_1234&pid_9abc&mi_00\9&5a6b7c8d&0&0000"), // Case doesn't matter.
            Capture(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256\4&1A2B3C4&0&0001"),
            Render(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256\4&1A2B3C4&0&0001"),
            Capture(@"USB\VID_17A0&PID_0310\USBMIC"),
        ];

        List<string> selected = PnpDeviceRules.SelectMicOnlyHardware(endpoints, _ => null);

        Assert.Equal([WebcamMic, @"USB\VID_17A0&PID_0310\USBMIC"], selected);
    }

    [Fact]
    public void SoftwareAndBluetoothMicrophonesAreNeverMicrophoneHardware()
    {
        AudioEndpointHardware[] endpoints =
        [
            Capture(@"ROOT\MEDIA\0000"),
            Capture(@"SWD\DRIVERENUM\{A1B2}#VIRTUALMIC&1"),
            Capture(@"SW\{96E080C7-143C-11D1-B40F-00A0C9223196}\VIRTUAL"),
            Capture(@"BTHHFENUM\BTHHFPAUDIO\D&1F2E3D4C&0&97"),
            Capture(@"BTHLEDEVICE\{0000184E-0000-1000-8000-00805F9B34FB}_DEV_VID&01004C\8&1&0"),
            Capture(string.Empty),
            Capture("garbage"),
        ];

        Assert.Empty(PnpDeviceRules.SelectMicOnlyHardware(endpoints, _ => null));
    }

    [Fact]
    public void AMicrophoneFunctionOfAHeadsetWithSpeakersOnAnotherFunctionIsNotMicrophoneHardware()
    {
        const string Chat = @"USB\VID_1038&PID_2202&MI_03\8&1&0&0003";
        const string Game = @"USB\VID_1038&PID_2202&MI_00\8&1&0&0000";
        const string WebcamParent = @"USB\VID_1234&PID_5678\00000001";
        const string WebcamParentsHub = @"USB\ROOT_HUB30\4&1&0&0";
        string? ParentOf(string id) => id switch
        {
            Chat or Game => @"USB\VID_1038&PID_2202\SERIAL",
            WebcamMic => WebcamParent,
            @"USB\VID_17A0&PID_0310\USBMIC" or @"USB\VID_046D&PID_0A44\SPEAKERS" => WebcamParentsHub,
            _ => null,
        };

        AudioEndpointHardware[] endpoints =
        [
            Capture(Chat),
            Render(Game),
            Capture(WebcamMic),
            Capture(@"USB\VID_17A0&PID_0310\USBMIC"),
            Render(@"USB\VID_046D&PID_0A44\SPEAKERS"), // A different device on the same hub: not a sibling function.
        ];

        Assert.Equal([WebcamMic, @"USB\VID_17A0&PID_0310\USBMIC"], PnpDeviceRules.SelectMicOnlyHardware(endpoints, ParentOf));
    }

    private static AudioEndpointHardware Capture(string id) => new(IsCapture: true, id);

    private static AudioEndpointHardware Render(string id) => new(IsCapture: false, id);
}
