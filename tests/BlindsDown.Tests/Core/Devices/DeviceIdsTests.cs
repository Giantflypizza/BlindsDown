using BlindsDown.Core.Devices;

namespace BlindsDown.Tests.Core.Devices;

public sealed class DeviceIdsTests
{
    [Theory]
    [InlineData(@"USB\VID_1234&PID_5678&MI_00\9&1A2B3C4D&0&0000", "USB")]
    [InlineData(@"swd\mmdevapi\{0.0.1.00000000}.{guid}", "SWD")]
    [InlineData("ROOT", "ROOT")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void EnumeratorIsTheFirstSegment(string? instanceId, string? expected) =>
        Assert.Equal(expected, DeviceIds.Enumerator(instanceId));

    [Theory]
    [InlineData(@"ROOT\MEDIA\0000", true)]
    [InlineData(@"SWD\MMDEVAPI\X", true)]
    [InlineData(@"sw\{guid}\Sonar", true)]
    [InlineData(@"USB\VID_1234&PID_5678&MI_00\9&1&0&0000", false)]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC\4&1&0&0001", false)]
    [InlineData(@"BTHENUM\{0000111e}\8&1", false)]
    [InlineData(@"SWDX\Y", false)]
    [InlineData(null, false)]
    public void VirtualHardwareIsSoftwareEnumerated(string? instanceId, bool expected) =>
        Assert.Equal(expected, DeviceIds.IsVirtualHardware(instanceId));

    [Theory]
    [InlineData(@"USB\VID_1234&PID_5678&MI_00\9&1A2B3C4D&0&0000", @"USB\VID_1234&PID_5678&MI_00")]
    [InlineData(@"usb\vid_1234&pid_5678&mi_00\A&1234ABCD&0&0000", @"USB\VID_1234&PID_5678&MI_00")]
    [InlineData("NOSLASH", "NOSLASH")]
    [InlineData(@"\LEADING", @"\LEADING")]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    public void HardwareKeyDropsThePortSpecificPart(string? instanceId, string expected) =>
        Assert.Equal(expected, DeviceIds.HardwareKey(instanceId));

    [Fact]
    public void StableKeySurvivesAPortChangeButNotAModelChange()
    {
        string port1 = DeviceIds.StableMicKey(@"USB\VID_1234&PID_5678&MI_00\9&1A2B3C4D&0&0000", "Microphone");
        string port2 = DeviceIds.StableMicKey(@"USB\VID_1234&PID_5678&MI_00\A&1234ABCD&0&0000", " microphone ");
        string otherEndpoint = DeviceIds.StableMicKey(@"USB\VID_1234&PID_5678&MI_00\9&1A2B3C4D&0&0000", "Line In");

        Assert.Equal(@"mic|USB\VID_1234&PID_5678&MI_00|MICROPHONE", port1);
        Assert.Equal(port1, port2);
        Assert.NotEqual(port1, otherEndpoint);
        Assert.Equal("mic||MICROPHONE (USB HEADSET)", DeviceIds.StableMicKey(null, "Microphone (USB Headset)"));
    }

    [Theory]
    [InlineData(EndpointState.Active, true)]
    [InlineData(EndpointState.Unplugged, true)]
    [InlineData(EndpointState.Disabled, false)]
    [InlineData(EndpointState.NotPresent, false)]
    [InlineData(EndpointState.None, false)]
    public void UsableMeansActiveOrUnplugged(EndpointState state, bool usable) =>
        Assert.Equal(usable, new MicEndpoint { Id = "x", Name = "x", State = state }.IsUsable);

    [Fact]
    public void EndpointStableKeyFallsBackToTheNameAndFlagsVirtualDevices()
    {
        var sonar = new MicEndpoint { Id = "x", Name = "Microphone (Sonar)", State = EndpointState.Active, HardwareInstanceId = @"SWD\SONAR\1" };

        Assert.Equal(@"mic|SWD\SONAR|MICROPHONE (SONAR)", sonar.StableKey);
        Assert.True(sonar.IsVirtual);
    }

    [Fact]
    public void DefaultMicsMatchIgnoringCase()
    {
        var defaults = new DefaultMics("A", null, "c");

        Assert.True(defaults.Contains("a"));
        Assert.True(defaults.Contains("C"));
        Assert.False(defaults.Contains("b"));
        Assert.False(DefaultMics.None.Contains("a"));
    }

    [Fact]
    public void SnapshotFindIgnoresCase()
    {
        var snapshot = new AudioSnapshot([new MicEndpoint { Id = "{ABC}", Name = "x", State = EndpointState.Active }], DefaultMics.None, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot.Find("{abc}"));
        Assert.Null(snapshot.Find("{abd}"));
    }
}
