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
    /// context so our own changes can be told apart from other apps' changes. Throws on failure.
    /// </summary>
    void SetMute(string endpointId, bool muted);
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

    public DeviceOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
