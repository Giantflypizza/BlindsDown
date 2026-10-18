using BlindsDown.Core.Devices;

namespace BlindsDown.Tests.Core.Fakes;

/// <summary>One simulated capture endpoint. Mute persists across unplug/replug and disable/enable,
/// as it does for real Windows endpoints (it lives in the endpoint's property store). So does the
/// "turned off in Windows" switch: a USB mic that was off when it was unplugged comes back off.</summary>
internal sealed class FakeMic
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Whether the hardware is there: Active, Unplugged (an empty jack, a Bluetooth headset that is
    /// away) or NotPresent (USB device removed).</summary>
    public EndpointState Presence { get; set; } = EndpointState.Active;

    /// <summary>Turned off in Windows (Sound settings "Don't allow"), by the user or by BlindsDown.</summary>
    public bool IsOff { get; set; }

    /// <summary>
    /// What Windows reports: a removed device is NotPresent whether it is off or not; otherwise an endpoint
    /// that is off is Disabled. Setting it is the "make it exactly so" of the older tests: Disabled turns it
    /// off (a removed device comes back for that), anything else turns it on with that presence.
    /// </summary>
    public EndpointState State
    {
        get => Presence == EndpointState.NotPresent ? EndpointState.NotPresent : IsOff ? EndpointState.Disabled : Presence;
        set
        {
            if (value == EndpointState.Disabled)
            {
                IsOff = true;
                if (Presence == EndpointState.NotPresent)
                {
                    Presence = EndpointState.Active;
                }
            }
            else
            {
                IsOff = false;
                Presence = value;
            }
        }
    }

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

    /// <summary>SetEndpointEnabled throws a plain <see cref="DeviceOperationException"/> (not "access denied").</summary>
    public bool SetEnabledThrows { get; set; }

    /// <summary>SetEndpointEnabled(false) "succeeds" but the endpoint stays on.</summary>
    public bool TurnOffDoesNotStick { get; set; }

    /// <summary>SetEndpointEnabled(true) "succeeds" but the endpoint stays off.</summary>
    public bool TurnOnDoesNotStick { get; set; }

    /// <summary>Like Windows: active endpoints, and unplugged ones that can still be opened, expose their mute.
    /// One that is off can't be opened at all.</summary>
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

    public override string ToString() =>
        $"{Id}[{State}{(IsOff && State == EndpointState.NotPresent ? ",off" : string.Empty)}{(IsReadable ? string.Empty : ",unreadable")},{(Muted ? "muted" : "unmuted")}]";
}

/// <summary>A notification the real watcher would post to the engine thread.</summary>
internal sealed record FakeNotification(string EndpointId, bool IsMuteChange, bool Muted, bool CausedByUs);

/// <summary>
/// In-memory <see cref="IMicBackend"/>. Device events ("another app unmuted it", "unplugged") change the
/// fake devices and queue the notification the real watcher would deliver; tests decide when the engine
/// sees them (<see cref="LockWorld.Pump"/>), just like posts to the engine thread.
///
/// Defaults move like Windows moves them: when a default microphone stops being active (turned off,
/// unplugged), Windows picks another active one for that role (<see cref="DefaultFallback"/> when it is
/// active, otherwise the first active one by ID, otherwise none, and then the first one to become active again).
/// By default the pessimistic case: when the old default comes back, Windows does NOT switch back to it
/// (<see cref="ReturningDefaultTakesOver"/> models the other case).
/// </summary>
internal sealed class FakeMicBackend : IMicBackend
{
    private readonly List<FakeMic> _mics = [];
    private readonly Queue<FakeNotification> _notifications = new();
    private DefaultMics _defaults = DefaultMics.None;
    private DefaultMics _chosen = DefaultMics.None;

    /// <summary>The current defaults. Setting them is a choice (the user's, in Sound settings).</summary>
    public DefaultMics Defaults
    {
        get => _defaults;
        set => _defaults = _chosen = value;
    }

    /// <summary>Where Windows moves a default that stops being active, when this microphone is active.</summary>
    public string? DefaultFallback { get; set; }

    /// <summary>When a microphone that was chosen as a default becomes active again, Windows makes it that
    /// default again (it remembers the last choice per role). Off by default: the pessimistic case.</summary>
    public bool ReturningDefaultTakesOver { get; set; }

    public bool EnumerateThrows { get; set; }

    public bool DefaultsThrow { get; set; }

    /// <summary>When true, SetEndpointEnabled throws AccessDenied (Windows wants admin for it).</summary>
    public bool EndpointDisableDenied { get; set; }

    /// <summary>When true, SetEndpointEnabled throws NotSupported (an older Windows without the switch).</summary>
    public bool EndpointDisableNotSupported { get; set; }

    /// <summary>SetDefaultMic throws.</summary>
    public bool SetDefaultThrows { get; set; }

    /// <summary>SetDefaultMic "succeeds" but the default doesn't change.</summary>
    public bool SetDefaultDoesNotStick { get; set; }

    public CrashPlan? Crash { get; set; }

    /// <summary>Every SetMute call the engine made: (endpoint, muted, device state at the time).</summary>
    public List<(string Id, bool Muted, EndpointState State)> SetMuteLog { get; } = [];

    /// <summary>Every SetEndpointEnabled call the engine made, including refused ones.</summary>
    public List<(string Id, bool Enabled)> SetEndpointEnabledLog { get; } = [];

    /// <summary>Every SetDefaultMic call the engine made, including failed ones.</summary>
    public List<(string Id, MicRole Role)> SetDefaultLog { get; } = [];

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
            HardwareInstanceId = hardware ?? $@"USB\VID_1234&PID_{n:X4}&MI_00\9&{n:X8}&0&0000",
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

    /// <summary>A virtual microphone (a software device such as SteelSeries Sonar or VB-Cable).</summary>
    public FakeMic AddVirtual(string id, bool muted = false) => Add(id, muted, hardware: $@"SWD\MMDEVAPI\{{0.0.1.00000000}}.{{{id}}}");

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

    public void SetEndpointEnabled(string endpointId, bool enabled)
    {
        FakeMic? mic = Find(endpointId);
        SetEndpointEnabledLog.Add((endpointId, enabled));
        Crash?.Point($"before SetEndpointEnabled({endpointId}, {enabled})");
        if (mic is null)
        {
            throw new DeviceOperationException("No such endpoint (simulated).");
        }

        if (EndpointDisableDenied)
        {
            throw new DeviceOperationException("Access is denied (simulated).", DeviceErrorKind.AccessDenied);
        }

        if (EndpointDisableNotSupported)
        {
            throw new DeviceOperationException("Not supported (simulated).", DeviceErrorKind.NotSupported);
        }

        if (mic.SetEnabledThrows)
        {
            throw new DeviceOperationException("The device is busy (simulated).");
        }

        bool ignored = enabled ? mic.TurnOnDoesNotStick : mic.TurnOffDoesNotStick;
        if (!ignored)
        {
            Change(mic, () => mic.IsOff = !enabled);
        }

        Crash?.Point($"after SetEndpointEnabled({endpointId}, {enabled})");
    }

    public void SetDefaultMic(string endpointId, MicRole role)
    {
        SetDefaultLog.Add((endpointId, role));
        Crash?.Point($"before SetDefaultMic({endpointId}, {role})");
        if (SetDefaultThrows)
        {
            throw new DeviceOperationException("Setting the default failed (simulated).");
        }

        if (Find(endpointId) is not { State: EndpointState.Active })
        {
            throw new DeviceOperationException("Only an active microphone can be the default (simulated).");
        }

        if (!SetDefaultDoesNotStick)
        {
            Choose(endpointId, role);
        }

        Crash?.Point($"after SetDefaultMic({endpointId}, {role})");
    }

    /// <summary>The user makes a microphone the default for one role in Sound settings.</summary>
    public void UserSetsDefault(string id, MicRole role) => Choose(id, role);

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

    /// <summary>Sets exactly what Windows reports (see <see cref="FakeMic.State"/>).</summary>
    public void SetState(string id, EndpointState state)
    {
        FakeMic mic = this[id];
        Change(mic, () => mic.State = state);
    }

    /// <summary>The hardware is back. An endpoint that was off when it left comes back off.</summary>
    public void Plug(string id)
    {
        FakeMic mic = this[id];
        Change(mic, () => mic.Presence = EndpointState.Active);
    }

    /// <summary>USB devices disappear (NotPresent); jacks and Bluetooth hands-free endpoints become Unplugged.
    /// Whether the endpoint is turned off is kept.</summary>
    public void Unplug(string id, EndpointState to = EndpointState.NotPresent)
    {
        FakeMic mic = this[id];
        Change(mic, () => mic.Presence = to);
    }

    /// <summary>The user (or another app) turns the microphone off in Sound settings.</summary>
    public void Disable(string id) => SetState(id, EndpointState.Disabled);

    /// <summary>The user (or another app) turns the microphone back on in Sound settings.</summary>
    public void Enable(string id)
    {
        FakeMic mic = this[id];
        Change(mic, () => mic.IsOff = false);
    }

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

    public int SetEndpointEnabledCallsFor(string id) =>
        SetEndpointEnabledLog.Count(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies a device change like Windows: a state notification if the reported state changed, and
    /// defaults moved off a microphone that is no longer active.</summary>
    private void Change(FakeMic mic, Action change)
    {
        EndpointState before = mic.State;
        change();
        if (mic.State != before)
        {
            NotifyEndpoint(mic.Id);
        }

        _defaults = new DefaultMics(
            Settle(_defaults.Console, _chosen.Console),
            Settle(_defaults.Multimedia, _chosen.Multimedia),
            Settle(_defaults.Communications, _chosen.Communications));
    }

    private void Choose(string id, MicRole role)
    {
        _defaults = WithRole(_defaults, role, id);
        _chosen = WithRole(_chosen, role, id);
    }

    private static DefaultMics WithRole(DefaultMics defaults, MicRole role, string id) => role switch
    {
        MicRole.Console => defaults with { Console = id },
        MicRole.Multimedia => defaults with { Multimedia = id },
        _ => defaults with { Communications = id },
    };

    /// <summary>A role's default after a device change: the chosen one when it is back (if Windows does that),
    /// otherwise the current one while it stays active, otherwise a replacement. Like Windows, a role that lost its
    /// default because no microphone was active gets one again as soon as one is (a role that never had a default
    /// in a test stays without).</summary>
    private string? Settle(string? id, string? chosen)
    {
        if (ReturningDefaultTakesOver && chosen is not null && Find(chosen) is { State: EndpointState.Active })
        {
            return chosen;
        }

        if (id is null ? chosen is null : (Find(id) is not { } current || current.State == EndpointState.Active))
        {
            return id; // Never had a default, one this fake doesn't know, or still fine.
        }

        if (DefaultFallback is not null && Find(DefaultFallback) is { State: EndpointState.Active } fallback)
        {
            return fallback.Id;
        }

        return _mics
            .Where(m => m.State == EndpointState.Active)
            .Select(m => m.Id)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private FakeMic? Find(string id) =>
        _mics.Find(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
