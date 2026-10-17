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

    [Fact]
    public void Exclusion_MatchesByStableKeyOrLastKnownId()
    {
        DeviceLockInfo device = Device("{id-1}", @"mic|USB\VID_1038&PID_12E0&MI_00|MICROPHONE");

        Assert.True(DeviceExclusions.Matches(Exclusion(@"MIC|usb\vid_1038&pid_12e0&mi_00|microphone"), device, [device]));
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

    private static DeviceExclusion Exclusion(string stableKey, string? lastId = null) =>
        new() { StableKey = stableKey, LastKnownId = lastId, DisplayName = "Mic" };
}
