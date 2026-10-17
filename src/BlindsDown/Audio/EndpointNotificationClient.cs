using System.Runtime.InteropServices;
using BlindsDown.Core.Devices;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// Receives endpoint notifications from Windows and forwards them to the engine thread. Windows calls this on
/// its own threads and waits for each call, so every method only copies its arguments and queues the work:
/// it never blocks, never calls back into the audio API, and never registers or unregisters anything.
/// </summary>
internal sealed class EndpointNotificationClient : IMMNotificationClient
{
    private readonly EngineThread _engine;
    private readonly Action<string> _onEndpointChanged;
    private readonly Action? _onCaptureDefaultChanged;

    /// <param name="engine">The thread the handlers run on.</param>
    /// <param name="onEndpointChanged">An endpoint of either direction was added, removed or changed state.</param>
    /// <param name="onCaptureDefaultChanged">A default capture device changed (one call per role).</param>
    public EndpointNotificationClient(EngineThread engine, Action<string> onEndpointChanged, Action? onCaptureDefaultChanged)
    {
        _engine = engine;
        _onEndpointChanged = onEndpointChanged;
        _onCaptureDefaultChanged = onCaptureDefaultChanged;
    }

    public int OnDeviceStateChanged(nint deviceId, EndpointState newState) => ForwardEndpoint(deviceId);

    public int OnDeviceAdded(nint deviceId) => ForwardEndpoint(deviceId);

    public int OnDeviceRemoved(nint deviceId) => ForwardEndpoint(deviceId);

    public int OnDefaultDeviceChanged(DataFlow dataFlow, DeviceRole role, nint defaultDeviceId)
    {
        if (dataFlow == DataFlow.Capture && _onCaptureDefaultChanged is { } handler)
        {
            EngineDispatch.PostFromCallback(_engine, handler);
        }

        return CoreAudioErrors.SOk;
    }

    /// <summary>Ignored: fires constantly (levels, formats, names) and the periodic checks pick up anything relevant.</summary>
    public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => CoreAudioErrors.SOk;

    private int ForwardEndpoint(nint deviceId)
    {
        // The string belongs to Windows and is only valid during this call, so copy it now.
        string? id = Marshal.PtrToStringUni(deviceId);
        if (!string.IsNullOrEmpty(id))
        {
            EngineDispatch.PostFromCallback(_engine, () => _onEndpointChanged(id));
        }

        return CoreAudioErrors.SOk;
    }
}
