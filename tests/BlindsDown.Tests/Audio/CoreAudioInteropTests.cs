using System.Reflection;
using System.Runtime.InteropServices;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>Pure tests of the Core Audio interop declarations and helpers. No devices are touched.</summary>
public class CoreAudioInteropTests
{
    [Fact]
    public void PropVariantAndPropertyKeyHaveTheWindowsLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<PropVariant>());
        Assert.Equal(0, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.VarType)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Pointer)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.UInt32Value)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.BoolValue)));
        Assert.Equal(20, Marshal.SizeOf<PropertyKey>());
    }

    [Fact]
    public void PropVariantReadsAStringAndPropVariantClearFreesIt()
    {
        var value = new PropVariant
        {
            VarType = PropVariant.VtLpwstr,
            Pointer = Marshal.StringToCoTaskMemUni("Microphone (Arctis Nova Pro Wireless)"),
        };

        Assert.Equal("Microphone (Arctis Nova Pro Wireless)", value.AsString());
        Assert.Null(value.AsUInt32());
        Assert.Null(value.AsBoolean());
        Assert.Null(value.AsGuid());

        Assert.Equal(CoreAudioErrors.SOk, CoreAudioNative.PropVariantClear(ref value));
        Assert.Equal(PropVariant.VtEmpty, value.VarType);
        Assert.Equal(0, value.Pointer);
        Assert.Null(value.AsString());
    }

    [Fact]
    public void PropVariantReadsUInt32()
    {
        var value = new PropVariant { VarType = PropVariant.VtUInt32, UInt32Value = 0xFFFF_FFFE };

        Assert.Equal(0xFFFF_FFFEu, value.AsUInt32());
        Assert.Null(value.AsString());
        Assert.Null(value.AsBoolean());
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void PropVariantReadsVariantBool(short raw, bool expected)
    {
        var value = new PropVariant { VarType = PropVariant.VtBool, BoolValue = raw };

        Assert.Equal(expected, value.AsBoolean());
        Assert.Null(value.AsUInt32());
    }

    [Fact]
    public void PropVariantReadsAClsidAndPropVariantClearFreesIt()
    {
        var expected = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc");
        nint buffer = Marshal.AllocCoTaskMem(16);
        Marshal.StructureToPtr(expected, buffer, fDeleteOld: false);
        var value = new PropVariant { VarType = PropVariant.VtClsid, Pointer = buffer };

        Assert.Equal(expected, value.AsGuid());
        Assert.Null(value.AsString());

        Assert.Equal(CoreAudioErrors.SOk, CoreAudioNative.PropVariantClear(ref value));
        Assert.Equal(PropVariant.VtEmpty, value.VarType);
    }

    [Fact]
    public void EmptyPropVariantHasNoValue()
    {
        var value = default(PropVariant);

        Assert.Null(value.AsString());
        Assert.Null(value.AsUInt32());
        Assert.Null(value.AsBoolean());
        Assert.Null(value.AsGuid());
        Assert.Equal(CoreAudioErrors.SOk, CoreAudioNative.PropVariantClear(ref value));
    }

    [Theory]
    [InlineData(@"{1}.USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000", @"USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000")]
    [InlineData(@"{2}.ROOT\MEDIA\0001", @"ROOT\MEDIA\0001")]
    [InlineData(@"{1}.SWD\MMDEVAPI\{0.0.1.00000000}.{9f3c5a1e}", @"SWD\MMDEVAPI\{0.0.1.00000000}.{9f3c5a1e}")]
    [InlineData(@"  {1}.HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\4&2B7A3F&0&0001  ", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\4&2B7A3F&0&0001")]
    [InlineData(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001004c", @"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001004c")]
    [InlineData(@"{unterminated", @"{unterminated")]
    public void ControllerDeviceIdLosesItsPrefix(string raw, string expected) =>
        Assert.Equal(expected, EndpointReader.ParseControllerDeviceId(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{1}.")]
    public void EmptyControllerDeviceIdIsNull(string? raw) =>
        Assert.Null(EndpointReader.ParseControllerDeviceId(raw));

    [Theory]
    [InlineData(@"{1}.ROOT\MEDIA\0001", true)]
    [InlineData(@"{1}.SWD\MMDEVAPI\x", true)]
    [InlineData(@"{1}.USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000", false)]
    [InlineData(@"{1}.HDAUDIO\FUNC_01&VEN_10EC\4&1", false)]
    public void ParsedControllerIdFeedsTheVirtualCheck(string raw, bool isVirtual) =>
        Assert.Equal(isVirtual, DeviceIds.IsVirtualHardware(EndpointReader.ParseControllerDeviceId(raw)));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void VolumeNotificationReadsContextAndMuteAtTheirOffsets(int rawMuted, bool expectedMuted)
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA with two channels: GUID, BOOL, float, UINT, float[2].
        nint buffer = Marshal.AllocHGlobal(36);
        try
        {
            Marshal.StructureToPtr(CoreAudioMicBackend.EventContext, buffer, fDeleteOld: false);
            Marshal.WriteInt32(buffer, 16, rawMuted);
            Marshal.WriteInt32(buffer, 20, BitConverter.SingleToInt32Bits(0.75f));
            Marshal.WriteInt32(buffer, 24, 2);

            VolumeNotification notification = VolumeNotification.Read(buffer);

            Assert.Equal(CoreAudioMicBackend.EventContext, notification.EventContext);
            Assert.Equal(expectedMuted, notification.Muted);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ErrorsAreClassified()
    {
        Assert.True(CoreAudioErrors.IsServiceFailure(CoreAudioErrors.ServiceNotRunning));
        Assert.True(CoreAudioErrors.IsServiceFailure(unchecked((int)0x800706BA)));
        Assert.False(CoreAudioErrors.IsServiceFailure(CoreAudioErrors.NotFound));
        Assert.True(CoreAudioErrors.IsDeviceUnavailable(CoreAudioErrors.NotFound));
        Assert.True(CoreAudioErrors.IsDeviceUnavailable(CoreAudioErrors.DeviceInvalidated));
        Assert.False(CoreAudioErrors.IsDeviceUnavailable(CoreAudioErrors.ServiceNotRunning));

        // AUDCLNT_S_NO_SINGLE_PROCESS is a success code: GetProcessId's PID must still be used.
        Assert.True(CoreAudioErrors.NoSingleProcess > 0);
        CoreAudioErrors.Check(CoreAudioErrors.NoSingleProcess, "GetProcessId");

        var ex = Assert.Throws<CoreAudioException>(() => CoreAudioErrors.Check(CoreAudioErrors.ServiceNotRunning, "Muting"));
        Assert.Equal(CoreAudioErrors.ServiceNotRunning, ex.HResult);
        Assert.Contains("0x88890010", ex.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("Muting failed", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1234u, 1, 1234)] // S_OK, and IsSystemSoundsSession says S_FALSE: an ordinary app session
    [InlineData(0x0889000D, 4321u, 1, 4321)] // AUDCLNT_S_NO_SINGLE_PROCESS is a success code: keep the PID
    [InlineData(0, 1234u, 0, null)] // the system sounds session
    [InlineData(0, 0u, 1, null)] // PID 0: Windows itself
    [InlineData(unchecked((int)0x88890004), 1234u, 1, null)] // GetProcessId failed: the PID is not valid
    [InlineData(0, 1234u, unchecked((int)0x88890004), 1234)] // only S_OK marks system sounds; when in doubt, report the app
    public void SessionOwnerIsTheAppProcessUnlessItIsWindowsItself(int processIdResult, uint processId, int systemSoundsResult, int? expected) =>
        Assert.Equal(expected, MicSessionMonitor.AppProcessId(processIdResult, processId, systemSoundsResult));

    [Fact]
    public void EndpointNameFallsBackToTheDescription()
    {
        Assert.Equal("Mic (USB)", new EndpointProperties("Mic (USB)", "Mic", null, null).DisplayName);
        Assert.Equal("Mic", new EndpointProperties(null, "Mic", null, null).DisplayName);
        Assert.Equal("Microphone", EndpointProperties.Empty.DisplayName);
    }

    /// <summary>
    /// Classic COM interop maps methods to vtable slots purely by declaration order, and a wrong order calls
    /// the wrong native method. Slots 0-2 are IUnknown. Expected slots come from the Windows SDK headers.
    /// </summary>
    [Theory]
    [InlineData(typeof(IMMDeviceEnumerator), nameof(IMMDeviceEnumerator.GetDevice), 5)]
    [InlineData(typeof(IMMDeviceEnumerator), nameof(IMMDeviceEnumerator.UnregisterEndpointNotificationCallback), 7)]
    [InlineData(typeof(IMMDeviceCollection), nameof(IMMDeviceCollection.Item), 4)]
    [InlineData(typeof(IMMDevice), nameof(IMMDevice.GetState), 6)]
    [InlineData(typeof(IMMEndpoint), nameof(IMMEndpoint.GetDataFlow), 3)]
    [InlineData(typeof(IMMNotificationClient), nameof(IMMNotificationClient.OnDefaultDeviceChanged), 6)]
    [InlineData(typeof(IMMNotificationClient), nameof(IMMNotificationClient.OnPropertyValueChanged), 7)]
    [InlineData(typeof(IPropertyStore), nameof(IPropertyStore.GetValue), 5)]
    [InlineData(typeof(IAudioEndpointVolume), nameof(IAudioEndpointVolume.SetMute), 14)]
    [InlineData(typeof(IAudioEndpointVolume), nameof(IAudioEndpointVolume.GetMute), 15)]
    [InlineData(typeof(IAudioEndpointVolume), nameof(IAudioEndpointVolume.QueryHardwareSupport), 19)]
    [InlineData(typeof(IAudioEndpointVolume), nameof(IAudioEndpointVolume.GetVolumeRange), 20)]
    [InlineData(typeof(IAudioEndpointVolumeCallback), nameof(IAudioEndpointVolumeCallback.OnNotify), 3)]
    [InlineData(typeof(IAudioSessionManager2), nameof(IAudioSessionManager2.GetSessionEnumerator), 5)]
    [InlineData(typeof(IAudioSessionManager2), nameof(IAudioSessionManager2.RegisterSessionNotification), 6)]
    [InlineData(typeof(IAudioSessionManager2), nameof(IAudioSessionManager2.UnregisterSessionNotification), 7)]
    [InlineData(typeof(IAudioSessionEnumerator), nameof(IAudioSessionEnumerator.GetSession), 4)]
    [InlineData(typeof(IAudioSessionControl2), nameof(IAudioSessionControl2.RegisterAudioSessionNotification), 10)]
    [InlineData(typeof(IAudioSessionControl2), nameof(IAudioSessionControl2.GetSessionInstanceIdentifier), 13)]
    [InlineData(typeof(IAudioSessionControl2), nameof(IAudioSessionControl2.GetProcessId), 14)]
    [InlineData(typeof(IAudioSessionControl2), nameof(IAudioSessionControl2.IsSystemSoundsSession), 15)]
    [InlineData(typeof(IAudioSessionControl2), nameof(IAudioSessionControl2.SetDuckingPreference), 16)]
    [InlineData(typeof(IAudioSessionNotification), nameof(IAudioSessionNotification.OnSessionCreated), 3)]
    [InlineData(typeof(IAudioSessionEvents), nameof(IAudioSessionEvents.OnStateChanged), 8)]
    [InlineData(typeof(IAudioSessionEvents), nameof(IAudioSessionEvents.OnSessionDisconnected), 9)]
    public void ComMethodsSitInTheirWindowsVtableSlots(Type comInterface, string method, int slot)
    {
        // The runtime assigns slots from the first slot after IUnknown, in metadata (declaration) order.
        MethodInfo[] declared = [.. comInterface.GetMethods().OrderBy(m => m.MetadataToken)];
        int index = Array.FindIndex(declared, m => m.Name == method);

        Assert.True(index >= 0, $"{comInterface.Name}.{method} is not declared");
        Assert.Equal(slot, Marshal.GetStartComSlot(comInterface) + index);
    }

    [Theory]
    [InlineData(typeof(IMMDeviceEnumerator), 5)]
    [InlineData(typeof(IMMDeviceCollection), 2)]
    [InlineData(typeof(IMMDevice), 4)]
    [InlineData(typeof(IMMEndpoint), 1)]
    [InlineData(typeof(IMMNotificationClient), 5)]
    [InlineData(typeof(IPropertyStore), 5)]
    [InlineData(typeof(IAudioEndpointVolume), 18)]
    [InlineData(typeof(IAudioEndpointVolumeCallback), 1)]
    [InlineData(typeof(IAudioSessionManager2), 7)]
    [InlineData(typeof(IAudioSessionEnumerator), 2)]
    [InlineData(typeof(IAudioSessionControl2), 14)]
    [InlineData(typeof(IAudioSessionNotification), 1)]
    [InlineData(typeof(IAudioSessionEvents), 7)]
    public void ComInterfacesDeclareEveryWindowsMethod(Type comInterface, int methodCount)
    {
        Assert.Equal(methodCount, comInterface.GetMethods().Length);
        Assert.Equal(3, Marshal.GetStartComSlot(comInterface));
        Assert.Equal(2 + methodCount, Marshal.GetEndComSlot(comInterface));
    }
}
