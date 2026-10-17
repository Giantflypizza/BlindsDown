using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// Real <see cref="IMicBackend"/> on top of Windows Core Audio
/// (IMMDeviceEnumerator, IMMDevice, IPropertyStore, IAudioEndpointVolume).
/// Create and use only on the <see cref="Infrastructure.EngineThread"/> (MTA).
///
/// Every operation survives a restart of the Windows audio service (it reconnects and tries once more), and
/// failures reach the lock engine as <see cref="DeviceOperationException"/> with a readable reason.
/// </summary>
internal sealed class CoreAudioMicBackend : IMicBackend, IDisposable
{
    private readonly EndpointEnumerator _enumerator = new("Microphones");
    private readonly ProblemLog _problems = new("Microphones");

    /// <summary>Event context passed to SetMute so our own changes can be recognised in notifications.</summary>
    public static Guid EventContext { get; } = new("6f1d2b4e-8a43-4c1e-9d5b-2b3f7c0a9e11");

    public IReadOnlyList<MicEndpoint> EnumerateMics()
    {
        try
        {
            return _enumerator.WithRecovery(ReadAllMics);
        }
        catch (CoreAudioException ex)
        {
            throw new DeviceOperationException($"Could not list the microphones: {ex.Explanation}", ex);
        }
    }

    public DefaultMics GetDefaultMics()
    {
        try
        {
            return _enumerator.WithRecovery(() => new DefaultMics(
                _enumerator.DefaultCaptureEndpointId(DeviceRole.Console),
                _enumerator.DefaultCaptureEndpointId(DeviceRole.Multimedia),
                _enumerator.DefaultCaptureEndpointId(DeviceRole.Communications)));
        }
        catch (CoreAudioException ex)
        {
            throw new DeviceOperationException($"Could not read the default microphones: {ex.Explanation}", ex);
        }
    }

    public bool? GetMute(string endpointId)
    {
        try
        {
            return _enumerator.WithRecovery(() => ReadMute(endpointId));
        }
        catch (CoreAudioException ex)
        {
            throw new DeviceOperationException($"Could not read the mute state: {ex.Explanation}", ex);
        }
    }

    public void SetMute(string endpointId, bool muted)
    {
        try
        {
            _ = _enumerator.WithRecovery(() => WriteMute(endpointId, muted));
        }
        catch (CoreAudioException ex)
        {
            throw new DeviceOperationException(ex.Explanation, ex);
        }
    }

    public void Dispose() => _enumerator.Dispose();

    private List<MicEndpoint> ReadAllMics()
    {
        List<ComRef<IMMDevice>> devices = _enumerator.OpenCaptureDevices(CoreAudioConstants.AllStates);
        var mics = new List<MicEndpoint>(devices.Count);
        try
        {
            foreach (ComRef<IMMDevice> device in devices)
            {
                if (ReadMic(device.Value) is { } mic)
                {
                    mics.Add(mic);
                }
            }
        }
        finally
        {
            foreach (ComRef<IMMDevice> device in devices)
            {
                device.Dispose();
            }
        }

        // An empty list would read as "this PC has no microphones", which must never happen by accident.
        if (mics.Count == 0 && devices.Count > 0)
        {
            throw new CoreAudioException("Windows listed microphones, but none of them could be read");
        }

        return mics;
    }

    /// <summary>Reads one endpoint. Returns null only when the device object itself no longer works.</summary>
    private MicEndpoint? ReadMic(IMMDevice device)
    {
        string? id = EndpointReader.GetId(device);
        EndpointState? state = EndpointReader.GetState(device);
        if (id is null || state is null)
        {
            return null;
        }

        EndpointProperties properties = EndpointReader.ReadProperties(device);
        bool? muted = null;
        bool hardwareMute = false;
        if ((state.Value & CoreAudioConstants.ActivatableStates) != 0)
        {
            using ComRef<IAudioEndpointVolume>? volume = EndpointReader.TryActivate<IAudioEndpointVolume>(device, out int hr);
            if (volume is null)
            {
                _problems.Report("volume control of " + id, $"Could not open the volume control of \"{properties.DisplayName}\": {CoreAudioErrors.Describe(hr)}");
            }
            else
            {
                _problems.Resolve("volume control of " + id);
                if (volume.Value.GetMute(out int mute) >= 0)
                {
                    muted = mute != 0;
                }

                if (volume.Value.QueryHardwareSupport(out uint support) >= 0)
                {
                    hardwareMute = (support & CoreAudioConstants.HardwareSupportMute) != 0;
                }
            }
        }

        return new MicEndpoint
        {
            Id = id,
            Name = properties.DisplayName,
            EndpointDescription = properties.Description,
            State = state.Value,
            Muted = muted,
            HasHardwareMute = hardwareMute,
            HardwareInstanceId = properties.HardwareInstanceId,
        };
    }

    private bool? ReadMute(string endpointId)
    {
        using ComRef<IAudioEndpointVolume>? volume = OpenVolume(endpointId, out int hr);
        if (volume is null)
        {
            return UnavailableOrThrow(hr, "Opening the microphone's volume control");
        }

        hr = volume.Value.GetMute(out int mute);
        return hr >= 0 ? mute != 0 : UnavailableOrThrow(hr, "Reading the mute state");
    }

    private bool WriteMute(string endpointId, bool muted)
    {
        using ComRef<IAudioEndpointVolume>? volume = OpenVolume(endpointId, out int hr);
        if (volume is null)
        {
            throw new CoreAudioException("Opening the microphone's volume control", hr);
        }

        Guid context = EventContext;
        CoreAudioErrors.Check(volume.Value.SetMute(muted ? 1 : 0, ref context), muted ? "Muting" : "Unmuting");
        return true;
    }

    /// <summary>
    /// The endpoint's volume control, or null with the reason in <paramref name="hresult"/> (an "unavailable"
    /// code when the endpoint is unknown or not present). Throws only when the audio service fails.
    /// </summary>
    private ComRef<IAudioEndpointVolume>? OpenVolume(string endpointId, out int hresult)
    {
        using ComRef<IMMDevice>? device = _enumerator.OpenDevice(endpointId);
        EndpointState? state = device is null ? null : EndpointReader.GetState(device.Value);
        if (device is null || state is null || (state.Value & CoreAudioConstants.ActivatableStates) == 0)
        {
            hresult = CoreAudioErrors.NotFound;
            return null;
        }

        return EndpointReader.TryActivate<IAudioEndpointVolume>(device.Value, out hresult);
    }

    /// <summary>"Can't be opened right now" is a normal answer (null); anything else is a real error.</summary>
    private static bool? UnavailableOrThrow(int hr, string operation) =>
        CoreAudioErrors.IsDeviceUnavailable(hr) ? null : throw new CoreAudioException(operation, hr);
}
