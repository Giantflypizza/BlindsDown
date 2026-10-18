using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

// IPolicyConfig: the interface behind the Windows Sound settings, which uses it to pick default devices and for the
// "Allow" / "Don't allow" (Disable) switch of a device. It is not in the public SDK headers, but its IID and vtable
// have been the same since Windows 7 (Windows 10 1507 and 1511 briefly used other IIDs; .NET 10 doesn't run there).
// EarTrumpet, SoundSwitch and AudioDeviceCmdlets all declare it this way, and AudioSes.dll answers QueryInterface
// for this IID. Classic COM interop maps methods to vtable slots purely by declaration order, so every slot is
// declared even though BlindsDown calls only the last two.

/// <summary>
/// CPolicyConfigClient (AudioSes.dll, ThreadingModel "Both", so it is created directly on the engine thread). The
/// second and last COM class BlindsDown creates, next to <see cref="MMDeviceEnumeratorCoClass"/>.
/// </summary>
[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal sealed class PolicyConfigClientCoClass
{
}

/// <summary>
/// IPolicyConfig. BlindsDown only turns microphones off and on (<see cref="SetEndpointVisibility"/>) and puts
/// default microphones back (<see cref="SetDefaultEndpoint"/>). Every other slot is declared with raw pointer
/// parameters and is obsolete-as-error, so changing a device format, share mode or property cannot even be
/// compiled. (Their exact parameter lists differ between published declarations; only the number of slots
/// matters, and it is the same in all of them.)
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    /// <summary>GetMixFormat(PCWSTR deviceId, WAVEFORMATEX** format).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetMixFormat(nint deviceId, nint format);

    /// <summary>GetDeviceFormat(PCWSTR deviceId, BOOL useDefault, WAVEFORMATEX** format).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetDeviceFormat(nint deviceId, int useDefault, nint format);

    /// <summary>ResetDeviceFormat(PCWSTR deviceId).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int ResetDeviceFormat(nint deviceId);

    /// <summary>SetDeviceFormat(PCWSTR deviceId, WAVEFORMATEX* endpointFormat, WAVEFORMATEX* mixFormat).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetDeviceFormat(nint deviceId, nint endpointFormat, nint mixFormat);

    /// <summary>GetProcessingPeriod(PCWSTR deviceId, BOOL useDefault, INT64* defaultPeriod, INT64* minimumPeriod).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetProcessingPeriod(nint deviceId, int useDefault, nint defaultPeriod, nint minimumPeriod);

    /// <summary>SetProcessingPeriod(PCWSTR deviceId, INT64* period).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetProcessingPeriod(nint deviceId, nint period);

    /// <summary>GetShareMode(PCWSTR deviceId, DeviceShareMode* mode).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetShareMode(nint deviceId, nint mode);

    /// <summary>SetShareMode(PCWSTR deviceId, DeviceShareMode* mode).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetShareMode(nint deviceId, nint mode);

    /// <summary>GetPropertyValue(PCWSTR deviceId, const PROPERTYKEY&amp; key, PROPVARIANT* value).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetPropertyValue(nint deviceId, nint key, nint value);

    /// <summary>SetPropertyValue(PCWSTR deviceId, const PROPERTYKEY&amp; key, PROPVARIANT* value).</summary>
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetPropertyValue(nint deviceId, nint key, nint value);

    /// <summary>
    /// Makes <paramref name="deviceId"/> the default endpoint of its direction for one role, for every user of the
    /// PC (the Sound settings "Set as default"). Windows only accepts an active endpoint.
    /// </summary>
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceRole role);

    /// <summary>
    /// The Sound settings "Allow" (1) / "Don't allow" (0) switch, for every user of the PC: a hidden endpoint
    /// reports DEVICE_STATE_DISABLED and apps can't open it. Streams that are already open keep running, which is
    /// why BlindsDown mutes first. <paramref name="visible"/> is a Win32 BOOL.
    /// </summary>
    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}
