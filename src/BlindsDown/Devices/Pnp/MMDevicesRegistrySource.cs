using System.Security;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using Microsoft.Win32;

namespace BlindsDown.Devices.Pnp;

/// <summary>One audio endpoint as Windows records it: its direction and the hardware behind it.</summary>
internal readonly record struct AudioEndpointHardware(bool IsCapture, string HardwareInstanceId);

/// <summary>Where the Plug and Play layer learns which hardware has microphones and which has speakers.</summary>
internal interface IAudioEndpointHardwareSource
{
    /// <summary>Every audio endpoint Windows has set up, in every state (also unplugged and disabled ones).
    /// Throws <see cref="DeviceOperationException"/> when the list can't be read at all.</summary>
    IReadOnlyList<AudioEndpointHardware> Read();
}

/// <summary>
/// Reads Windows' own endpoint records, READ-ONLY: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\
/// Capture\{endpoint} and ...\Render\{endpoint}. Each endpoint's Properties key has
/// "{b3f8fa53-0004-438e-9003-51a46e139bfc},2" = "{n}." + the hardware instance ID: the property Core Audio reports
/// as <see cref="MicEndpoint.HardwareInstanceId"/>, parsed the same way (<see cref="EndpointReader.ParseControllerDeviceId"/>).
/// The registry, unlike Core Audio, needs neither the audio service nor a COM apartment, and the helper reads it
/// as LocalSystem in session 0.
/// </summary>
internal sealed class MMDevicesRegistrySource(RegistryKey hive, string audioKeyPath) : IAudioEndpointHardwareSource
{
    public const string WindowsAudioKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

    /// <summary>PKEY_Endpoint ... ,2: the endpoint's hardware ("{1}.USB\VID_...").</summary>
    public const string HardwareValueName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";

    public MMDevicesRegistrySource()
        : this(Registry.LocalMachine, WindowsAudioKeyPath)
    {
    }

    public IReadOnlyList<AudioEndpointHardware> Read()
    {
        var endpoints = new List<AudioEndpointHardware>();
        ReadFlow(endpoints, "Capture", isCapture: true);
        ReadFlow(endpoints, "Render", isCapture: false);
        return endpoints;
    }

    private void ReadFlow(List<AudioEndpointHardware> endpoints, string flow, bool isCapture)
    {
        string[] endpointKeys;
        try
        {
            using RegistryKey? flowKey = hive.OpenSubKey(audioKeyPath + @"\" + flow);
            if (flowKey is null)
            {
                return; // No endpoint of this kind was ever set up.
            }

            endpointKeys = flowKey.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new DeviceOperationException($"Could not read Windows' list of audio devices: {ex.Message}", ex);
        }

        foreach (string endpointKey in endpointKeys)
        {
            if (ReadHardwareId(flow, endpointKey) is { } hardwareId)
            {
                endpoints.Add(new AudioEndpointHardware(isCapture, hardwareId));
            }
        }
    }

    /// <summary>
    /// Null when the endpoint has no hardware value or vanished while being read (a missing key reads as null; a
    /// key deleted while open throws IOException). An endpoint that exists but can't be read is an error, not a
    /// gap: skipping a headset's unreadable headphones endpoint would make its microphone look like mic-only
    /// hardware.
    /// </summary>
    private string? ReadHardwareId(string flow, string endpointKey)
    {
        try
        {
            using RegistryKey? properties = hive.OpenSubKey($@"{audioKeyPath}\{flow}\{endpointKey}\Properties");
            return properties?.GetValue(HardwareValueName) is string value ? EndpointReader.ParseControllerDeviceId(value) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new DeviceOperationException($"Could not read Windows' audio device {endpointKey}: {ex.Message}", ex);
        }
    }
}
