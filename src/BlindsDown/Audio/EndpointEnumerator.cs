using System.Runtime.InteropServices;
using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// One component's IMMDeviceEnumerator, created on first use and created again after the Windows audio
/// service restarted (objects from before a restart keep failing with RPC errors). An optional notification
/// client is registered on every enumerator this creates, so notifications survive such a restart.
///
/// The enumerator wrapper itself is never released explicitly: the runtime may share it with other
/// components that created one too, and releasing it would break them. Dropping it lets the garbage collector
/// release it, which is fine for an object that is replaced only after a service restart.
///
/// Use only on the engine thread.
/// </summary>
internal sealed class EndpointEnumerator : IDisposable
{
    private const string ServiceTopic = "audio service";

    private readonly IMMNotificationClient? _notificationClient;
    private readonly ProblemLog _problems;
    private IMMDeviceEnumerator? _enumerator;
    private bool _clientRegistered;

    /// <param name="component">Name used in log messages.</param>
    /// <param name="notificationClient">Registered on every enumerator this creates. This object holds the strong
    /// reference that keeps it alive while Windows may still call it.</param>
    public EndpointEnumerator(string component, IMMNotificationClient? notificationClient = null)
    {
        _notificationClient = notificationClient;
        _problems = new ProblemLog(component);
    }

    /// <summary>
    /// Runs <paramref name="operation"/>. If it fails because the audio service went away, creates a fresh
    /// enumerator (re-registering notifications) and runs it once more. Other failures pass through.
    /// </summary>
    public T WithRecovery<T>(Func<T> operation)
    {
        T result;
        try
        {
            result = operation();
        }
        catch (CoreAudioException ex) when (CoreAudioErrors.IsServiceFailure(ex.HResult))
        {
            _problems.Report(ServiceTopic, $"{ex.Message}. Reconnecting to the audio service.");
            Reset();
            result = operation();
        }

        _problems.Resolve(ServiceTopic);
        return result;
    }

    /// <inheritdoc cref="WithRecovery{T}(Func{T})"/>
    public void WithRecovery(Action operation) =>
        _ = WithRecovery(() =>
        {
            operation();
            return true;
        });

    /// <summary>Opens every capture endpoint whose state is in <paramref name="stateMask"/>. The caller disposes them.</summary>
    public List<ComRef<IMMDevice>> OpenCaptureDevices(EndpointState stateMask)
    {
        IMMDeviceEnumerator enumerator = Current();
        CoreAudioErrors.Check(enumerator.EnumAudioEndpoints(DataFlow.Capture, stateMask, out nint collectionPointer), "Listing microphones");
        using ComRef<IMMDeviceCollection> collection = ComRef.Take<IMMDeviceCollection>(collectionPointer, "Listing microphones");
        CoreAudioErrors.Check(collection.Value.GetCount(out uint count), "Counting microphones");

        var devices = new List<ComRef<IMMDevice>>((int)count);
        for (uint i = 0; i < count; i++)
        {
            // A device can vanish between GetCount and Item; just skip it.
            if (collection.Value.Item(i, out nint devicePointer) >= 0 && ComRef.TryTake<IMMDevice>(devicePointer) is { } device)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    /// <summary>Opens an endpoint by ID (any direction), or returns null when Windows does not know it.</summary>
    public ComRef<IMMDevice>? OpenDevice(string endpointId)
    {
        int hr = Current().GetDevice(endpointId, out nint pointer);
        if (CoreAudioErrors.IsDeviceUnavailable(hr))
        {
            return null;
        }

        CoreAudioErrors.Check(hr, "Opening an audio device");
        return ComRef.TryTake<IMMDevice>(pointer);
    }

    /// <summary>The default capture endpoint's ID for <paramref name="role"/>, or null when the role has none.</summary>
    public string? DefaultCaptureEndpointId(DeviceRole role)
    {
        int hr = Current().GetDefaultAudioEndpoint(DataFlow.Capture, role, out nint pointer);
        if (hr == CoreAudioErrors.NotFound)
        {
            return null;
        }

        CoreAudioErrors.Check(hr, "Reading the default microphone");
        using ComRef<IMMDevice>? device = ComRef.TryTake<IMMDevice>(pointer);
        return device is null ? null : EndpointReader.GetId(device.Value);
    }

    /// <summary>Drops the current enumerator (unregistering notifications); the next call creates a fresh one.</summary>
    public void Reset()
    {
        if (_enumerator is not null && _clientRegistered)
        {
            // Fails harmlessly when the audio service is already gone.
            _ = _enumerator.UnregisterEndpointNotificationCallback(_notificationClient!);
        }

        _clientRegistered = false;
        _enumerator = null;
    }

    public void Dispose() => Reset();

    private IMMDeviceEnumerator Current()
    {
        if (_enumerator is not null)
        {
            return _enumerator;
        }

        IMMDeviceEnumerator enumerator;
        try
        {
            // CoCreateInstance, then a QueryInterface for IMMDeviceEnumerator on the new COM object.
            object instance = new MMDeviceEnumeratorCoClass();
            enumerator = (IMMDeviceEnumerator)instance;
        }
        catch (COMException ex)
        {
            throw new CoreAudioException("Starting the audio device enumerator", ex.HResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CoreAudioException("Starting the audio device enumerator", ex.HResult);
        }

        _enumerator = enumerator;
        if (_notificationClient is not null)
        {
            int hr = enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            _clientRegistered = hr >= 0;
            if (_clientRegistered)
            {
                _problems.Resolve("device notifications");
            }
            else
            {
                _problems.Report("device notifications", $"Could not subscribe to device changes: {CoreAudioErrors.Describe(hr)}. Changes are still found by the periodic check.");
            }
        }

        return enumerator;
    }
}
