using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// A pretend Windows for <see cref="EndpointSwitch"/>: endpoints, default microphones, and switches that can fail,
/// take a few polls to show, or be ignored. Never touches real devices.
/// </summary>
internal sealed class FakeSwitchPort : IEndpointSwitchPort
{
    private readonly Dictionary<string, EndpointState> _stateBeforeDisable = new(StringComparer.OrdinalIgnoreCase);
    private Action? _pendingChange;
    private int _pausesUntilChange;

    public Dictionary<string, EndpointSwitchStatus> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<DeviceRole, string?> Defaults { get; } = new()
    {
        [DeviceRole.Console] = null,
        [DeviceRole.Multimedia] = null,
        [DeviceRole.Communications] = null,
    };

    /// <summary>HRESULT returned by SetEndpointVisibility (the change happens only on success).</summary>
    public int VisibilityResult { get; set; }

    /// <summary>HRESULT returned by SetDefaultEndpoint.</summary>
    public int DefaultResult { get; set; }

    /// <summary>Number of Pause calls before an accepted change becomes visible.</summary>
    public int PausesBeforeChangeShows { get; set; }

    /// <summary>Windows says yes, but nothing changes.</summary>
    public bool IgnoreChanges { get; set; }

    /// <summary>The endpoint disappears (not present) instead of changing.</summary>
    public bool VanishOnChange { get; set; }

    public List<(string Id, bool Visible)> VisibilityCalls { get; } = [];

    public List<(string Id, DeviceRole Role)> DefaultCalls { get; } = [];

    public List<TimeSpan> Pauses { get; } = [];

    public int Reads { get; private set; }

    public FakeSwitchPort With(string id, EndpointState state, DataFlow? flow = DataFlow.Capture)
    {
        Endpoints[id] = new EndpointSwitchStatus(state, flow);
        return this;
    }

    public EndpointSwitchStatus? ReadEndpoint(string endpointId)
    {
        Reads++;
        return Endpoints.TryGetValue(endpointId, out EndpointSwitchStatus status) ? status : null;
    }

    public int SetEndpointVisibility(string endpointId, bool visible)
    {
        VisibilityCalls.Add((endpointId, visible));
        if (VisibilityResult >= 0)
        {
            Schedule(() =>
            {
                EndpointSwitchStatus current = Endpoints[endpointId];
                if (VanishOnChange)
                {
                    Endpoints[endpointId] = current with { State = EndpointState.NotPresent };
                }
                else if (!visible)
                {
                    _stateBeforeDisable[endpointId] = current.State;
                    Endpoints[endpointId] = current with { State = EndpointState.Disabled };
                }
                else
                {
                    Endpoints[endpointId] = current with
                    {
                        State = _stateBeforeDisable.TryGetValue(endpointId, out EndpointState before) ? before : EndpointState.Active,
                    };
                }
            });
        }

        return VisibilityResult;
    }

    public string? ReadDefaultMic(DeviceRole role) => Defaults[role];

    public int SetDefaultEndpoint(string endpointId, DeviceRole role)
    {
        DefaultCalls.Add((endpointId, role));
        if (DefaultResult >= 0)
        {
            Schedule(() => Defaults[role] = endpointId);
        }

        return DefaultResult;
    }

    public void Pause(TimeSpan delay)
    {
        Pauses.Add(delay);
        if (_pendingChange is not null && --_pausesUntilChange <= 0)
        {
            Apply();
        }
    }

    /// <summary>Marks an endpoint as disabled by "someone else", remembering what re-enabling restores.</summary>
    public void DisabledEarlierFrom(string id, EndpointState before)
    {
        _stateBeforeDisable[id] = before;
        Endpoints[id] = Endpoints[id] with { State = EndpointState.Disabled };
    }

    private void Schedule(Action change)
    {
        if (IgnoreChanges)
        {
            return;
        }

        _pendingChange = change;
        _pausesUntilChange = PausesBeforeChangeShows;
        if (_pausesUntilChange <= 0)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Action? change = _pendingChange;
        _pendingChange = null;
        change?.Invoke();
    }
}
