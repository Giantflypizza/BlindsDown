using BlindsDown.Devices.Pnp;
using BlindsDown.Tests.SystemServices;
using Microsoft.Win32;

namespace BlindsDown.Tests.Pnp;

/// <summary>Writes only under a throwaway HKCU\Software\BlindsDownTests\{guid} key laid out like Windows'
/// MMDevices\Audio key; the real one is only read (in <see cref="RealPnpTests"/>).</summary>
public sealed class MMDevicesRegistrySourceTests
{
    [Fact]
    public void EndpointHardwareIsReadFromCaptureAndRenderKeys()
    {
        using var test = new TestRegistryKey();
        Endpoint(test, @"Capture\{a1b2c3d4-0000-4000-8000-000000000001}", @"{1}.USB\VID_1234&PID_5678&MI_02\7&1A2B3C4D&0&0002");
        Endpoint(test, @"Capture\{a1b2c3d4-0000-4000-8000-000000000002}", @"{1}.USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000");
        Endpoint(test, @"Render\{a1b2c3d4-0000-4000-8000-000000000003}", @"{1}.USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000");
        Endpoint(test, @"Render\{a1b2c3d4-0000-4000-8000-000000000004}", @"ROOT\MEDIA\0000"); // No "{n}." prefix: kept as is.

        IReadOnlyList<AudioEndpointHardware> endpoints = new MMDevicesRegistrySource(Registry.CurrentUser, test.Path).Read();

        Assert.Equal(4, endpoints.Count);
        Assert.Contains(new AudioEndpointHardware(true, @"USB\VID_1234&PID_5678&MI_02\7&1A2B3C4D&0&0002"), endpoints);
        Assert.Contains(new AudioEndpointHardware(true, @"USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000"), endpoints);
        Assert.Contains(new AudioEndpointHardware(false, @"USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000"), endpoints);
        Assert.Contains(new AudioEndpointHardware(false, @"ROOT\MEDIA\0000"), endpoints);
    }

    [Fact]
    public void EndpointsWithoutAUsableHardwareValueAreSkipped()
    {
        using var test = new TestRegistryKey();
        Endpoint(test, @"Capture\{good}", @"{1}.USB\VID_0001&PID_0001\1");
        using (test.Key.CreateSubKey(@"Capture\{no-properties}"))
        {
        }

        using (RegistryKey properties = test.Key.CreateSubKey(@"Capture\{no-value}\Properties"))
        {
            properties.SetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2", "Microphone");
        }

        using (RegistryKey properties = test.Key.CreateSubKey(@"Capture\{number}\Properties"))
        {
            properties.SetValue(MMDevicesRegistrySource.HardwareValueName, 7, RegistryValueKind.DWord);
        }

        Endpoint(test, @"Capture\{empty}", string.Empty);
        Endpoint(test, @"Capture\{prefix-only}", "{1}.");

        IReadOnlyList<AudioEndpointHardware> endpoints = new MMDevicesRegistrySource(Registry.CurrentUser, test.Path).Read();

        Assert.Equal([new AudioEndpointHardware(true, @"USB\VID_0001&PID_0001\1")], endpoints);
    }

    [Fact]
    public void MissingKeysMeanNoEndpoints()
    {
        using var test = new TestRegistryKey();

        Assert.Empty(new MMDevicesRegistrySource(Registry.CurrentUser, test.Path).Read());
        Assert.Empty(new MMDevicesRegistrySource(Registry.CurrentUser, test.Path + @"\Missing").Read());

        Endpoint(test, @"Capture\{only-capture}", @"{1}.USB\VID_0001&PID_0001\1");
        Assert.Single(new MMDevicesRegistrySource(Registry.CurrentUser, test.Path).Read());
    }

    private static void Endpoint(TestRegistryKey test, string endpointPath, string hardwareValue)
    {
        using RegistryKey properties = test.Key.CreateSubKey(endpointPath + @"\Properties");
        properties.SetValue(MMDevicesRegistrySource.HardwareValueName, hardwareValue, RegistryValueKind.String);
    }
}
