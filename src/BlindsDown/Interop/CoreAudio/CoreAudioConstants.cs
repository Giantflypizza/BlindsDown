using BlindsDown.Core.Devices;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>EDataFlow: the direction of an audio endpoint.</summary>
internal enum DataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

/// <summary>
/// ERole: Windows keeps a separate default device per role, so "the default microphone" is really three
/// settings (apps for calls usually follow <see cref="Communications"/>, everything else <see cref="Console"/>).
/// </summary>
internal enum DeviceRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>AudioSessionState.</summary>
internal enum AudioSessionState
{
    Inactive = 0,

    /// <summary>At least one stream of the session is running, i.e. the app is capturing right now.</summary>
    Active = 1,

    Expired = 2,
}

/// <summary>AudioSessionDisconnectReason (why Windows dropped a session).</summary>
internal enum AudioSessionDisconnectReason
{
    DeviceRemoval = 0,
    ServerShutdown = 1,
    FormatChanged = 2,
    SessionLogoff = 3,
    SessionDisconnected = 4,
    ExclusiveModeOverride = 5,
}

/// <summary>Plain numeric constants from the Windows SDK headers that the Core Audio calls take.</summary>
internal static class CoreAudioConstants
{
    /// <summary>
    /// CLSCTX_INPROC_SERVER. The endpoint objects BlindsDown activates (volume control, session manager)
    /// live in-process (MMDevAPI.dll / AudioSes.dll), so there is no reason to allow any other context.
    /// </summary>
    public const uint ClsCtxInprocServer = 0x1;

    /// <summary>STGM_READ: endpoint property stores are only ever opened read-only.</summary>
    public const uint StgmRead = 0x0;

    /// <summary>ENDPOINT_HARDWARE_SUPPORT_MUTE bit of IAudioEndpointVolume::QueryHardwareSupport.</summary>
    public const uint HardwareSupportMute = 0x2;

    /// <summary>Every DEVICE_STATE_* value (DEVICE_STATEMASK_ALL).</summary>
    public const EndpointState AllStates =
        EndpointState.Active | EndpointState.Disabled | EndpointState.NotPresent | EndpointState.Unplugged;

    /// <summary>
    /// States in which Windows lets an endpoint's volume control be activated. NOTPRESENT endpoints are
    /// only remembered entries in the registry; activating anything on them fails.
    /// </summary>
    public const EndpointState ActivatableStates =
        EndpointState.Active | EndpointState.Disabled | EndpointState.Unplugged;
}
