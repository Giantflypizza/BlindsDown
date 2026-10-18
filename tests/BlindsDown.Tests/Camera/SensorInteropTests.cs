using System.Reflection;
using System.Runtime.InteropServices;
using BlindsDown.Interop.Media;

namespace BlindsDown.Tests.Camera;

/// <summary>
/// The Media Foundation sensor activity declarations against mfidl.h. Classic COM interop maps methods to vtable
/// slots by declaration order, so a wrong order calls the wrong native method (and a wrong IID makes every
/// QueryInterface fail). Pure reflection and layout checks: nothing is created.
/// </summary>
public class SensorInteropTests
{
    public static TheoryData<Type, string, string[]> Interfaces => new()
    {
        { typeof(IMFSensorActivityMonitor), "D0CEF145-B3F4-4340-A2E5-7A5080CA05CB", ["Start", "Stop"] },
        { typeof(IMFSensorActivitiesReportCallback), "DE5072EE-DBE3-46DC-8A87-B6F631194751", ["OnActivitiesReport"] },
        { typeof(IMFSensorActivitiesReport), "683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777", ["GetCount", "GetActivityReport", "GetActivityReportByDeviceName"] },
        { typeof(IMFSensorActivityReport), "3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD", ["GetFriendlyName", "GetSymbolicLink", "GetProcessCount", "GetProcessActivity"] },
        { typeof(IMFSensorProcessActivity), "39DC7F4A-B141-4719-813C-A7F46162A2B8", ["GetProcessId", "GetStreamingState", "GetStreamingMode", "GetReportTime"] },
        { typeof(IMFShutdown), "97EC2EA4-0E42-4937-97AC-9D6D328824E1", ["Shutdown", "GetShutdownStatus"] },
    };

    [Fact]
    public void OnlyShutdownIsCalledOnIMFShutdown()
    {
        Assert.Null(typeof(IMFShutdown).GetMethod(nameof(IMFShutdown.Shutdown))!.GetCustomAttribute<ObsoleteAttribute>());
        Assert.True(typeof(IMFShutdown).GetMethod("GetShutdownStatus")!.GetCustomAttribute<ObsoleteAttribute>()?.IsError);
    }

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void InterfacesMatchMfidl(Type comInterface, string iid, string[] methods)
    {
        MethodInfo[] declared = [.. comInterface.GetMethods().OrderBy(m => m.MetadataToken)];

        Assert.True(comInterface.IsImport);
        Assert.Equal(ComInterfaceType.InterfaceIsIUnknown, comInterface.GetCustomAttribute<InterfaceTypeAttribute>()?.Value);
        Assert.Equal(new Guid(iid), comInterface.GUID);
        Assert.Equal(methods, declared.Select(m => m.Name));
        Assert.Equal(3, Marshal.GetStartComSlot(comInterface));
        Assert.Equal(2 + methods.Length, Marshal.GetEndComSlot(comInterface));
        Assert.All(declared, m =>
        {
            Assert.Equal(typeof(int), m.ReturnType);
            Assert.True((m.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0, $"{m.Name} must return its HRESULT");
        });
    }

    [Theory]
    [InlineData(typeof(IMFSensorActivitiesReport), nameof(IMFSensorActivitiesReport.GetActivityReport), new[] { typeof(uint), typeof(nint) })]
    [InlineData(typeof(IMFSensorActivityReport), nameof(IMFSensorActivityReport.GetSymbolicLink), new[] { typeof(nint), typeof(uint), typeof(uint) })]
    [InlineData(typeof(IMFSensorActivityReport), nameof(IMFSensorActivityReport.GetFriendlyName), new[] { typeof(nint), typeof(uint), typeof(uint) })]
    [InlineData(typeof(IMFSensorActivityReport), nameof(IMFSensorActivityReport.GetProcessActivity), new[] { typeof(uint), typeof(nint) })]
    [InlineData(typeof(IMFSensorProcessActivity), nameof(IMFSensorProcessActivity.GetProcessId), new[] { typeof(uint) })]
    [InlineData(typeof(IMFSensorProcessActivity), nameof(IMFSensorProcessActivity.GetStreamingState), new[] { typeof(int) })]
    [InlineData(typeof(IMFSensorProcessActivity), nameof(IMFSensorProcessActivity.GetReportTime), new[] { typeof(long) })]
    [InlineData(typeof(IMFSensorActivitiesReportCallback), nameof(IMFSensorActivitiesReportCallback.OnActivitiesReport), new[] { typeof(nint) })]
    public void ParametersHaveTheNativeSizes(Type comInterface, string method, Type[] parameters)
    {
        ParameterInfo[] declared = comInterface.GetMethod(method)!.GetParameters();

        Assert.Equal(parameters, declared.Select(p => p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType));
    }

    [Fact]
    public void MediaFoundationConstantsAreTheSdkOnes()
    {
        Assert.Equal(0x00020070u, SensorNative.MfVersion);
        Assert.Equal(1u, SensorNative.MfStartupLite);
        Assert.Equal(0, (int)SensorDeviceMode.Controller);
        Assert.Equal(1, (int)SensorDeviceMode.Shared);
    }

    [Fact]
    public void DevicePropertyStructuresHaveTheWindowsLayout()
    {
        Assert.Equal(20, Marshal.SizeOf<CameraPropertyKey>());
        Assert.Equal(16, (int)Marshal.OffsetOf<CameraPropertyKey>(nameof(CameraPropertyKey.PropertyId)));
        Assert.Equal(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), CameraPropertyKey.InstanceId.FormatId);
        Assert.Equal(256u, CameraPropertyKey.InstanceId.PropertyId);
        Assert.Equal(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), CameraPropertyKey.Name.FormatId);
        Assert.Equal(10u, CameraPropertyKey.Name.PropertyId);

        // SERVICE_STATUS_PROCESS: nine DWORDs, dwProcessId at offset 28.
        Assert.Equal(36, Marshal.SizeOf<CameraServiceStatus>());
        Assert.Equal(28, (int)Marshal.OffsetOf<CameraServiceStatus>(nameof(CameraServiceStatus.ProcessId)));
    }

    [Fact]
    public void DeviceStringsAreDecodedUpToTheTerminator()
    {
        byte[] value = [.. "USB\\VID_046D&PID_085C&MI_00\\7&1A2B3C4D&0&0000\0"u8.ToArray().SelectMany(b => new[] { b, (byte)0 })];

        Assert.Equal(@"USB\VID_046D&PID_085C&MI_00\7&1A2B3C4D&0&0000", CameraDeviceLookup.DecodeString(CameraLookupNative.DevPropTypeString, value, (uint)value.Length));
    }

    [Fact]
    public void DeviceStringsNeverReadPastTheirSize()
    {
        byte[] value = [.. "Camera\0"u8.ToArray().SelectMany(b => new[] { b, (byte)0 })];

        // No terminator within the reported size: only the reported bytes are used.
        Assert.Equal("Cam", CameraDeviceLookup.DecodeString(CameraLookupNative.DevPropTypeString, value, 6));

        // A size larger than the buffer is clamped to the buffer.
        Assert.Equal("Camera", CameraDeviceLookup.DecodeString(CameraLookupNative.DevPropTypeString, value, 4096));
    }

    [Theory]
    [InlineData(0x12u, "\0", null)] // empty
    [InlineData(0x12u, "   \0", null)] // blank
    [InlineData(0x12u, " HD Webcam \0", "HD Webcam")]
    [InlineData(0x2012u, "A\0B\0\0", null)] // DEVPROP_TYPE_STRING_LIST is not a single string
    [InlineData(0x07u, "x\0", null)] // DEVPROP_TYPE_UINT32
    public void OnlyNonEmptyStringValuesAreUsed(uint type, string text, string? expected)
    {
        byte[] value = [.. text.SelectMany(c => BitConverter.GetBytes(c))];

        Assert.Equal(expected, CameraDeviceLookup.DecodeString(type, value, (uint)value.Length));
    }
}
