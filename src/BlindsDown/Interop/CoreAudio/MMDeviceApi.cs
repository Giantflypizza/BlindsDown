using System.Runtime.InteropServices;
using BlindsDown.Core.Devices;

namespace BlindsDown.Interop.CoreAudio;

// MMDevAPI (mmdeviceapi.h): the endpoint enumerator, endpoints and endpoint notifications.
// Classic COM interop cannot express interface inheritance, so every interface lists its methods in exact
// vtable order. Methods that return objects or strings use raw pointers ("out nint"): the caller wraps objects
// in ComRef (deterministic release) and frees strings itself, and nothing is read unless the HRESULT succeeded.

/// <summary>
/// The Windows audio device enumerator (CLSID_MMDeviceEnumerator). One of only two COM classes BlindsDown ever
/// creates (the other is <see cref="PolicyConfigClientCoClass"/>); every other audio object is obtained from it.
/// </summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorCoClass
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>Returns an IMMDeviceCollection.</summary>
    [PreserveSig]
    int EnumAudioEndpoints(DataFlow dataFlow, EndpointState stateMask, out nint devices);

    /// <summary>Returns an IMMDevice, or E_NOTFOUND when the role has no default device.</summary>
    [PreserveSig]
    int GetDefaultAudioEndpoint(DataFlow dataFlow, DeviceRole role, out nint endpoint);

    /// <summary>Returns an IMMDevice for an endpoint ID, or E_NOTFOUND.</summary>
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    /// <summary>Returns an IMMDevice.</summary>
    [PreserveSig]
    int Item(uint index, out nint device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>Creates an endpoint object (e.g. IAudioEndpointVolume). <paramref name="activationParams"/> must be 0.</summary>
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, nint activationParams, out nint instance);

    /// <summary>Returns an IPropertyStore. BlindsDown always passes STGM_READ.</summary>
    [PreserveSig]
    int OpenPropertyStore(uint access, out nint properties);

    /// <summary>Returns a CoTaskMemAlloc'ed string that the caller frees.</summary>
    [PreserveSig]
    int GetId(out nint id);

    [PreserveSig]
    int GetState(out EndpointState state);
}

/// <summary>Implemented by every endpoint object next to IMMDevice; tells render and capture endpoints apart.</summary>
[ComImport]
[Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpoint
{
    [PreserveSig]
    int GetDataFlow(out DataFlow dataFlow);
}

/// <summary>
/// Endpoint notifications, implemented by BlindsDown. Windows calls these on its own threads; implementations
/// must only queue work and return (calling back into the audio API from here can deadlock). The string
/// arguments are borrowed and valid only during the call.
/// </summary>
[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(nint deviceId, EndpointState newState);

    [PreserveSig]
    int OnDeviceAdded(nint deviceId);

    [PreserveSig]
    int OnDeviceRemoved(nint deviceId);

    /// <summary><paramref name="defaultDeviceId"/> is null when the role no longer has a default device.</summary>
    [PreserveSig]
    int OnDefaultDeviceChanged(DataFlow dataFlow, DeviceRole role, nint defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}
