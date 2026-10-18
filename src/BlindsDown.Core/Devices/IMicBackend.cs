namespace BlindsDown.Core.Devices;

/// <summary>
/// Everything the lock engine needs from the Windows audio system. The real implementation
/// wraps Core Audio (IMMDeviceEnumerator / IAudioEndpointVolume); tests use a fake.
/// All calls happen on one dedicated thread.
/// </summary>
public interface IMicBackend
{
    /// <summary>All capture endpoints in every state (active, disabled, not present, unplugged).</summary>
    IReadOnlyList<MicEndpoint> EnumerateMics();

    DefaultMics GetDefaultMics();

    /// <summary>Current mute state, or null when the endpoint can't be opened (e.g. not present).</summary>
    bool? GetMute(string endpointId);

    /// <summary>
    /// Mutes or unmutes an endpoint. Implementations tag the change with BlindsDown's own event
    /// context so BlindsDown's own changes can be told apart from other apps' changes. Throws on failure.
    /// </summary>
    void SetMute(string endpointId, bool muted);

    /// <summary>
    /// Turns a microphone on or off in Windows: the Sound settings "Allow" / "Don't allow" switch
    /// (IPolicyConfig::SetEndpointVisibility). Off, apps can't open it at all. Throws
    /// <see cref="DeviceOperationException"/> (with <see cref="DeviceErrorKind.AccessDenied"/> when Windows
    /// requires administrator rights for it).
    /// </summary>
    void SetEndpointEnabled(string endpointId, bool enabled);

    /// <summary>Makes a microphone the Windows default for one role (IPolicyConfig::SetDefaultEndpoint).</summary>
    void SetDefaultMic(string endpointId, MicRole role);
}

/// <summary>The three Windows default-device roles (ERole).</summary>
public enum MicRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

public enum DeviceErrorKind
{
    Failed,

    /// <summary>Windows refused because administrator rights are required.</summary>
    AccessDenied,

    /// <summary>The operation isn't supported for this device or on this Windows version.</summary>
    NotSupported,
}

/// <summary>Thrown by a backend when a device operation fails.</summary>
public sealed class DeviceOperationException : Exception
{
    public DeviceOperationException()
    {
    }

    public DeviceOperationException(string message)
        : base(message)
    {
    }

    public DeviceOperationException(string message, DeviceErrorKind kind)
        : base(message)
    {
        ErrorKind = kind;
    }

    public DeviceErrorKind ErrorKind { get; }

    public DeviceOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
