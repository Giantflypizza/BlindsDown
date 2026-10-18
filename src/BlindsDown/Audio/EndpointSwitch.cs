using System.Globalization;
using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>An endpoint as <see cref="EndpointSwitch"/> sees it. <see cref="Flow"/> is null when Windows didn't say.</summary>
internal readonly record struct EndpointSwitchStatus(EndpointState State, DataFlow? Flow);

/// <summary>
/// What <see cref="EndpointSwitch"/> needs from Windows. The real one is <see cref="CoreAudioSwitchPort"/>; tests use
/// a fake, because the switches change real devices for every user of the PC and must never run in a test.
/// Reads throw <see cref="CoreAudioException"/> when the audio service fails; the setters return the HRESULT.
/// </summary>
internal interface IEndpointSwitchPort
{
    /// <summary>The endpoint's state and direction, or null when Windows doesn't know the ID.</summary>
    EndpointSwitchStatus? ReadEndpoint(string endpointId);

    int SetEndpointVisibility(string endpointId, bool visible);

    /// <summary>The default capture endpoint for <paramref name="role"/>, or null when there is none.</summary>
    string? ReadDefaultMic(DeviceRole role);

    int SetDefaultEndpoint(string endpointId, DeviceRole role);

    /// <summary>Waits before the next verification read.</summary>
    void Pause(TimeSpan delay);
}

/// <summary>
/// Turns a microphone off or on in Windows and puts a default microphone back, the careful way: check first,
/// change, then verify by reading the result back. Nothing is changed when the endpoint is unknown, isn't a
/// microphone or isn't connected, or when it already is as requested (so a repeated call is harmless, and the
/// retry after an audio-service restart is safe).
///
/// Failures become <see cref="DeviceOperationException"/> whose message is the reason, written to follow
/// "Could not turn off "X": ...". A failure of the audio service itself stays a <see cref="CoreAudioException"/>,
/// so the caller's <see cref="EndpointEnumerator.WithRecovery{T}(Func{T})"/> reconnects and tries once more.
/// </summary>
internal static class EndpointSwitch
{
    /// <summary>
    /// How often the result is read back. The audio service usually applies a change before the call returns, but
    /// its state reaches other processes through notifications, so a short wait is allowed before calling it failed.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Reads after the first one before a change counts as "did not take effect" (2 s in total).</summary>
    public const int MaxPolls = 40;

    /// <summary>
    /// Turns <paramref name="endpointId"/> on (<paramref name="enabled"/>) or off in Windows and verifies it: off
    /// means DEVICE_STATE_DISABLED, on means active or unplugged (a jack microphone with nothing plugged in).
    /// </summary>
    public static void SetEnabled(IEndpointSwitchPort port, string endpointId, bool enabled)
    {
        EndpointSwitchStatus status = ReadMicrophone(port, endpointId);
        if (status.State == EndpointState.NotPresent)
        {
            throw new DeviceOperationException(
                enabled ? "it isn't connected, so Windows can't turn it back on" : "it isn't connected, so Windows can't turn it off",
                DeviceErrorKind.Failed);
        }

        if (IsSwitched(status.State, enabled))
        {
            return;
        }

        ThrowIfFailed(port.SetEndpointVisibility(endpointId, enabled), enabled ? "Turning the microphone on" : "Turning the microphone off");
        if (!WaitFor(port, () => port.ReadEndpoint(endpointId) is { } now && IsSwitched(now.State, enabled)))
        {
            throw new DeviceOperationException(
                port.ReadEndpoint(endpointId) is null or { State: EndpointState.NotPresent }
                    ? "the microphone disappeared while Windows was changing it"
                    : enabled
                        ? "Windows accepted the change, but the microphone is still turned off"
                        : "Windows accepted the change, but the microphone is still turned on",
                DeviceErrorKind.Failed);
        }
    }

    /// <summary>
    /// Makes <paramref name="endpointId"/> the default microphone for <paramref name="role"/> and verifies it with
    /// GetDefaultAudioEndpoint. Windows only accepts an active microphone as a default.
    /// </summary>
    public static void SetDefault(IEndpointSwitchPort port, string endpointId, DeviceRole role)
    {
        EndpointSwitchStatus status = ReadMicrophone(port, endpointId);
        if (status.State != EndpointState.Active)
        {
            throw new DeviceOperationException("it isn't available right now, so Windows can't make it the default microphone", DeviceErrorKind.Failed);
        }

        if (IsDefault(port, endpointId, role))
        {
            return;
        }

        ThrowIfFailed(port.SetDefaultEndpoint(endpointId, role), "Setting the default microphone");
        if (!WaitFor(port, () => IsDefault(port, endpointId, role)))
        {
            throw new DeviceOperationException("Windows accepted the change, but another microphone is still the default", DeviceErrorKind.Failed);
        }
    }

    /// <summary>
    /// What a failure HRESULT means for the lock: <see cref="DeviceErrorKind.AccessDenied"/> when Windows wants
    /// administrator rights, <see cref="DeviceErrorKind.NotSupported"/> when this Windows doesn't have the switch
    /// (the class or interface is missing, or the method isn't implemented), otherwise
    /// <see cref="DeviceErrorKind.Failed"/>.
    /// </summary>
    public static DeviceErrorKind ErrorKindFor(int hr) => hr switch
    {
        CoreAudioErrors.AccessDenied or CoreAudioErrors.PrivilegeNotHeld => DeviceErrorKind.AccessDenied,
        CoreAudioErrors.NotImplemented or CoreAudioErrors.ClassNotRegistered or CoreAudioErrors.NoInterface => DeviceErrorKind.NotSupported,
        _ => DeviceErrorKind.Failed,
    };

    /// <summary>The reason shown to the user for a failure HRESULT.</summary>
    public static string Explain(int hr)
    {
        string code = "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
        return ErrorKindFor(hr) switch
        {
            DeviceErrorKind.AccessDenied => $"Windows requires administrator rights for this ({code})",
            DeviceErrorKind.NotSupported => $"this version of Windows doesn't support it ({code})",
            _ => CoreAudioErrors.Describe(hr),
        };
    }

    /// <summary>True when <paramref name="state"/> already is what "<paramref name="enabled"/>" asks for.</summary>
    public static bool IsSwitched(EndpointState state, bool enabled) =>
        enabled ? state is EndpointState.Active or EndpointState.Unplugged : state == EndpointState.Disabled;

    /// <summary>
    /// Reads the endpoint and refuses anything that isn't a known microphone. The direction check means a mix-up of
    /// IDs can never turn off speakers or change the default playback device.
    /// </summary>
    private static EndpointSwitchStatus ReadMicrophone(IEndpointSwitchPort port, string endpointId)
    {
        EndpointSwitchStatus status = port.ReadEndpoint(endpointId)
            ?? throw new DeviceOperationException("Windows no longer lists this microphone", DeviceErrorKind.Failed);
        return status.Flow == DataFlow.Capture
            ? status
            : throw new DeviceOperationException("Windows doesn't list it as a microphone, so BlindsDown leaves it alone", DeviceErrorKind.Failed);
    }

    private static bool IsDefault(IEndpointSwitchPort port, string endpointId, DeviceRole role) =>
        string.Equals(port.ReadDefaultMic(role), endpointId, StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr >= 0)
        {
            return;
        }

        if (CoreAudioErrors.IsServiceFailure(hr))
        {
            throw new CoreAudioException(operation, hr);
        }

        throw new DeviceOperationException(Explain(hr), ErrorKindFor(hr));
    }

    /// <summary>True as soon as <paramref name="done"/> holds; false after <see cref="MaxPolls"/> more reads.</summary>
    private static bool WaitFor(IEndpointSwitchPort port, Func<bool> done)
    {
        for (int poll = 0; ; poll++)
        {
            if (done())
            {
                return true;
            }

            if (poll >= MaxPolls)
            {
                return false;
            }

            port.Pause(PollInterval);
        }
    }
}

/// <summary>
/// The real <see cref="IEndpointSwitchPort"/>: reads through the backend's <see cref="EndpointEnumerator"/> (a
/// fresh IMMDevice for every read, so a cached state can't fool the verification) and changes through
/// <see cref="EndpointPolicyClient"/>. Use only on the engine thread.
/// </summary>
internal sealed class CoreAudioSwitchPort(EndpointEnumerator enumerator, EndpointPolicyClient policy) : IEndpointSwitchPort
{
    public EndpointSwitchStatus? ReadEndpoint(string endpointId)
    {
        using ComRef<IMMDevice>? device = enumerator.OpenDevice(endpointId);
        if (device is null || EndpointReader.GetState(device.Value) is not { } state)
        {
            return null;
        }

        return new EndpointSwitchStatus(state, EndpointReader.GetDataFlow(device.Value));
    }

    public int SetEndpointVisibility(string endpointId, bool visible) => policy.SetEndpointVisibility(endpointId, visible);

    public string? ReadDefaultMic(DeviceRole role) => enumerator.DefaultCaptureEndpointId(role);

    public int SetDefaultEndpoint(string endpointId, DeviceRole role) => policy.SetDefaultEndpoint(endpointId, role);

    public void Pause(TimeSpan delay) => Thread.Sleep(delay);
}
