using BlindsDown.Core.Devices;

namespace BlindsDown.Tests.Core.Fakes;

/// <summary>One simulated capture endpoint. Mute persists across unplug/replug and disable/enable,
/// as it does for real Windows endpoints (it lives in the endpoint's property store).</summary>
internal sealed class FakeMic
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public EndpointState State { get; set; } = EndpointState.Active;

    public bool Muted { get; set; }

    public bool HasHardwareMute { get; set; }

    public string? HardwareInstanceId { get; set; }

    /// <summary>A Bluetooth hands-free or jack endpoint that cannot be opened while unplugged (GetMute returns null).</summary>
    public bool UnreadableWhenUnplugged { get; set; }

    /// <summary>SetMute throws <see cref="DeviceOperationException"/>.</summary>
    public bool SetMuteThrows { get; set; }

    /// <summary>SetMute(true) "succeeds" but nothing changes (driver ignores it).</summary>
    public bool MuteDoesNotStick { get; set; }

    /// <summary>SetMute(false) "succeeds" but nothing changes.</summary>
    public bool UnmuteDoesNotStick { get; set; }

    /// <summary>GetMute throws <see cref="DeviceOperationException"/>.</summary>
    public bool GetMuteThrows { get; set; }

    /// <summary>Like Windows: active endpoints, and unplugged ones that can still be opened, expose their mute.</summary>
    public bool IsReadable =>
        State == EndpointState.Active || (State == EndpointState.Unplugged && !UnreadableWhenUnplugged);

    public MicEndpoint ToEndpoint() => new()
    {
        Id = Id,
        Name = Name,
        EndpointDescription = Description,
        State = State,
        Muted = IsReadable ? Muted : null,
        HasHardwareMute = HasHardwareMute,
        HardwareInstanceId = HardwareInstanceId,
    };

    public override string ToString() => $"{Id}[{State}{(IsReadable ? string.Empty : ",unreadable")},{(Muted ? "muted" : "unmuted")}]";
}

/// <summary>A notification the real watcher would post to the engine thread.</summary>
internal sealed record FakeNotification(string EndpointId, bool IsMuteChange, bool Muted, bool CausedByUs);

/// <summary>
/// In-memory <see cref="IMicBackend"/>. Device events ("another app unmuted it", "unplugged") change the
/// fake devices and queue the notification the real watcher would deliver; tests decide when the engine
/// sees them (<see cref="LockWorld.Pump"/>), just like posts to the engine thread.
/// </summary>
internal sealed class FakeMicBackend : IMicBackend
{
    private readonly List<FakeMic> _mics = [];
    private readonly Queue<FakeNotification> _notifications = new();

    public DefaultMics Defaults { get; set; } = DefaultMics.None;

    public bool EnumerateThrows { get; set; }

    public bool DefaultsThrow { get; set; }

    public CrashPlan? Crash { get; set; }

    /// <summary>Every SetMute call the engine made: (endpoint, muted, device state at the time).</summary>
    public List<(string Id, bool Muted, EndpointState State)> SetMuteLog { get; } = [];

    public IReadOnlyList<FakeMic> All => _mics;

    public FakeMic this[string id] =>
        _mics.Find(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(id);

    public FakeMic Add(
        string id,
        bool muted = false,
        EndpointState state = EndpointState.Active,
        string? hardware = null,
        string? description = null,
        bool hardwareMute = false,
        bool unreadableWhenUnplugged = false,
        bool notify = false)
    {
        int n = _mics.Count + 1;
        var mic = new FakeMic
        {
            Id = id,
            Name = $"Microphone ({id})",
            Description = description ?? "Microphone",
            State = state,
            Muted = muted,
            HardwareInstanceId = hardware ?? $@"USB\VID_1038&PID_{n:X4}&MI_00\9&{n:X8}&0&0000",
            HasHardwareMute = hardwareMute,
            UnreadableWhenUnplugged = unreadableWhenUnplugged,
        };
        _mics.Add(mic);
        if (notify)
        {
            NotifyEndpoint(id);
        }

        return mic;
    }

    public IReadOnlyList<MicEndpoint> EnumerateMics()
    {
        if (EnumerateThrows)
        {
            throw new DeviceOperationException("Enumeration failed (simulated).");
        }

        return _mics.Select(m => m.ToEndpoint()).ToList();
    }

    public DefaultMics GetDefaultMics() =>
        DefaultsThrow ? throw new DeviceOperationException("No default device (simulated).") : Defaults;

    public bool? GetMute(string endpointId)
    {
        FakeMic? mic = Find(endpointId);
        if (mic is null || !mic.IsReadable)
        {
            return null;
        }

        if (mic.GetMuteThrows)
        {
            throw new DeviceOperationException("GetMute failed (simulated).");
        }

        return mic.Muted;
    }

    public void SetMute(string endpointId, bool muted)
    {
        FakeMic? mic = Find(endpointId);
        SetMuteLog.Add((endpointId, muted, mic?.State ?? EndpointState.NotPresent));
        Crash?.Point($"before SetMute({endpointId}, {muted})");
        if (mic is null || !mic.IsReadable)
        {
            throw new DeviceOperationException("The device is not available (simulated).");
        }

        if (mic.SetMuteThrows)
        {
            throw new DeviceOperationException("Access denied (simulated).");
        }

        bool ignored = muted ? mic.MuteDoesNotStick : mic.UnmuteDoesNotStick;
        if (!ignored && mic.Muted != muted)
        {
            mic.Muted = muted;
            _notifications.Enqueue(new FakeNotification(mic.Id, true, muted, CausedByUs: true));
        }

        Crash?.Point($"after SetMute({endpointId}, {muted})");
    }

    /// <summary>Another app (or the user in Sound settings) changes the mute.</summary>
    public void OtherAppSetsMute(string id, bool muted)
    {
        FakeMic mic = this[id];
        if (!mic.IsReadable)
        {
            throw new InvalidOperationException($"{id} can't be opened right now.");
        }

        if (mic.Muted != muted)
        {
            mic.Muted = muted;
            _notifications.Enqueue(new FakeNotification(mic.Id, true, muted, CausedByUs: false));
        }
    }

    public void SetState(string id, EndpointState state)
    {
        FakeMic mic = this[id];
        if (mic.State != state)
        {
            mic.State = state;
            NotifyEndpoint(id);
        }
    }

    public void Plug(string id) => SetState(id, EndpointState.Active);

    /// <summary>USB devices disappear (NotPresent); jacks and Bluetooth hands-free endpoints become Unplugged.</summary>
    public void Unplug(string id, EndpointState to = EndpointState.NotPresent) => SetState(id, to);

    public void Disable(string id) => SetState(id, EndpointState.Disabled);

    public void Enable(string id) => SetState(id, EndpointState.Active);

    /// <summary>
    /// The same hardware plugged into another USB port: Windows creates a new endpoint (new ID, same
    /// hardware key, same description) and the old one becomes NotPresent.
    /// </summary>
    public FakeMic MoveToAnotherPort(string id, string newId)
    {
        FakeMic old = this[id];
        string hardware = old.HardwareInstanceId ?? string.Empty;
        string moved = hardware[..(hardware.LastIndexOf('\\') + 1)] + "A&1234ABCD&0&0000";
        FakeMic mic = Add(newId, muted: false, hardware: moved, description: old.Description, hardwareMute: old.HasHardwareMute);
        mic.Name = old.Name;
        SetState(id, EndpointState.NotPresent);
        NotifyEndpoint(newId);
        return mic;
    }

    public void NotifyEndpoint(string id) => _notifications.Enqueue(new FakeNotification(id, false, false, false));

    public bool TryDequeue(out FakeNotification notification) => _notifications.TryDequeue(out notification!);

    /// <summary>Notifications that were in flight when the process died are lost.</summary>
    public void DropNotifications() => _notifications.Clear();

    public int SetMuteCallsFor(string id) =>
        SetMuteLog.Count(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    private FakeMic? Find(string id) =>
        _mics.Find(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
