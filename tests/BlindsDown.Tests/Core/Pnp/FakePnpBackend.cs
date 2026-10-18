using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Pnp;

/// <summary>
/// One simulated PnP device, modelled on how Windows treats disables:
///  * <see cref="Disabled"/>: the device node is disabled right now (problem code 22), by anyone.
///  * <see cref="DisabledPersistently"/>: the "disabled" config flag that Device Manager writes. It survives an
///    unplug and a restart. BlindsDown's own disable never sets it, so a device BlindsDown turned off comes back
///    ON when it is plugged back in or the PC restarts (unless Windows made that disable persistent anyway,
///    <see cref="DisableMakesPersistent"/>).
/// </summary>
internal sealed class FakePnpDevice
{
    public required string InstanceId { get; init; }

    public required string Name { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Camera;

    public CameraType? CameraType { get; set; } = global::BlindsDown.Core.Locking.CameraType.Color;

    public bool IsVirtual { get; set; }

    public bool IsPresent { get; set; } = true;

    public bool Disabled { get; set; }

    public bool DisabledPersistently { get; set; }

    /// <summary>Results the next Disable calls return instead of working (one per call).</summary>
    public Queue<PnpChangeResult> DisableResults { get; } = new();

    /// <summary>Every Disable call returns this instead of working (e.g. an app holds the camera forever).</summary>
    public PnpChangeResult? DisableAlways { get; set; }

    /// <summary>Disable says Done but nothing changes (a driver that ignores it).</summary>
    public bool DisableDoesNotStick { get; set; }

    /// <summary>
    /// Windows makes BlindsDown's disable persistent and the backend can't take that back (never expected): the
    /// device is off for good, and Disable says so (<see cref="PnpChange.MadePersistent"/>).
    /// </summary>
    public bool DisableMakesPersistent { get; set; }

    public Queue<PnpChangeResult> EnableResults { get; } = new();

    public PnpChangeResult? EnableAlways { get; set; }

    /// <summary>Disable throws like a broken backend.</summary>
    public bool DisableThrows { get; set; }

    /// <summary>The state the device had when a test started, to compare against after unlock.</summary>
    public (bool Disabled, bool Persistently) Original { get; set; }

    public PnpDevice ToPnpDevice() => new()
    {
        InstanceId = InstanceId,
        Name = Name,
        Kind = Kind,
        CameraType = Kind == DeviceKind.Camera ? CameraType : null,
        IsPresent = IsPresent,
        IsStarted = IsPresent && !Disabled,
        IsDisabled = IsPresent ? Disabled : DisabledPersistently,
        ProblemCode = IsPresent && Disabled ? 22 : 0,
        IsVirtual = IsVirtual,
    };

    public override string ToString() =>
        $"{InstanceId}[{(IsPresent ? "present" : "away")},{(Disabled ? "off" : "on")}{(DisabledPersistently ? ",persistent" : string.Empty)}]";
}

/// <summary>
/// In-memory <see cref="IPnpBackend"/>. Device events (unplug, someone enabling a device) change the fake
/// devices and count a notification; tests decide when the engine sees them (<see cref="PnpWorld.Pump"/>), just
/// like posts to the engine thread. Disable and Enable pass crash points before and after the change.
/// </summary>
internal sealed class FakePnpBackend : IPnpBackend
{
    private readonly List<FakePnpDevice> _devices = [];

    public CrashPlan? Crash { get; set; }

    public bool EnumerateThrows { get; set; }

    /// <summary>Notifications the watcher would have posted since the last <see cref="TakeNotifications"/>.</summary>
    public int PendingNotifications { get; private set; }

    public List<string> DisableCalls { get; } = [];

    public List<string> EnableCalls { get; } = [];

    public int EnumerateCalls { get; private set; }

    public IReadOnlyList<FakePnpDevice> All => _devices;

    /// <summary>A device by its full instance ID or by the short name it was added with (the ID's last part).</summary>
    public FakePnpDevice this[string id] =>
        Find(id)
        ?? _devices.Find(d => d.InstanceId.EndsWith("\\" + id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(id);

    /// <summary>A camera ("cam" kinds) or microphone hardware with a realistic USB instance ID.</summary>
    public FakePnpDevice Add(
        string id,
        DeviceKind kind = DeviceKind.Camera,
        CameraType? type = CameraType.Color,
        bool present = true,
        bool disabledByUser = false,
        bool isVirtual = false,
        string? name = null,
        string? instanceId = null)
    {
        int n = _devices.Count + 1;
        var device = new FakePnpDevice
        {
            InstanceId = instanceId ?? (isVirtual ? $@"SWD\VCAMDEVAPI\{id}" : $@"USB\VID_ABCD&PID_{n:X4}&MI_00\{id}"),
            Name = name ?? $"Camera {id}",
            Kind = kind,
            CameraType = type,
            IsVirtual = isVirtual,
            IsPresent = present,
            Disabled = disabledByUser && present,
            DisabledPersistently = disabledByUser,
        };
        device.Original = (device.Disabled, device.DisabledPersistently);
        _devices.Add(device);
        return device;
    }

    public FakePnpDevice AddMic(string id, bool present = true, bool disabledByUser = false, bool isVirtual = false, string? instanceId = null) =>
        Add(id, DeviceKind.MicrophoneHardware, type: null, present, disabledByUser, isVirtual, $"Microphone {id}", instanceId);

    public IReadOnlyList<PnpDevice> Enumerate()
    {
        EnumerateCalls++;
        if (EnumerateThrows)
        {
            throw new DeviceOperationException("CM_Get_Device_Interface_List failed (simulated).");
        }

        return _devices.Select(d => d.ToPnpDevice()).ToList();
    }

    public PnpChange Disable(string instanceId)
    {
        FakePnpDevice? device = Find(instanceId);
        Crash?.Point($"before disable {instanceId}");
        DisableCalls.Add(instanceId);
        if (device is null || !device.IsPresent)
        {
            return new PnpChange(PnpChangeResult.NotFound);
        }

        if (device.DisableThrows)
        {
            throw new DeviceOperationException("CM_Disable_DevNode returned CR_FAILURE (simulated).");
        }

        if (device.DisableResults.TryDequeue(out PnpChangeResult queued) && queued != PnpChangeResult.Done)
        {
            return new PnpChange(queued, "simulated " + queued);
        }

        if (device.DisableAlways is { } always && always != PnpChangeResult.Done)
        {
            return new PnpChange(always, "simulated " + always);
        }

        bool madePersistent = false;
        if (!device.DisableDoesNotStick && !device.Disabled)
        {
            device.Disabled = true;
            madePersistent = device.DisableMakesPersistent;
            device.DisabledPersistently |= madePersistent;
            PendingNotifications++;
        }

        Crash?.Point($"after disable {instanceId}");
        return madePersistent ? PnpChange.Done with { MadePersistent = true } : PnpChange.Done;
    }

    /// <summary>The Enable calls told that a persistent disable is BlindsDown's own.</summary>
    public List<string> ClearPersistentCalls { get; } = [];

    /// <summary>Like the real backend: a device that is on is left alone, and so is one disabled persistently, unless
    /// the caller says that persistent disable is BlindsDown's (<paramref name="clearPersistent"/>).</summary>
    public PnpChange Enable(string instanceId, bool clearPersistent = false)
    {
        FakePnpDevice? device = Find(instanceId);
        Crash?.Point($"before enable {instanceId}");
        EnableCalls.Add(instanceId);
        if (device is null || !device.IsPresent)
        {
            return new PnpChange(PnpChangeResult.NotFound);
        }

        if (device.EnableResults.TryDequeue(out PnpChangeResult queued) && queued != PnpChangeResult.Done)
        {
            return new PnpChange(queued, "simulated " + queued);
        }

        if (device.EnableAlways is { } always && always != PnpChangeResult.Done)
        {
            return new PnpChange(always, "simulated " + always);
        }

        if (device.Disabled && device.DisabledPersistently && !clearPersistent)
        {
            return new PnpChange(PnpChangeResult.KeptOff, "simulated KeptOff");
        }

        // A device that is on is left as it is (also one with a persistent disable waiting for a restart).
        // CM_Enable_DevNode also clears the persistent "disabled" flag.
        if (device.Disabled)
        {
            if (clearPersistent)
            {
                ClearPersistentCalls.Add(instanceId);
            }

            device.Disabled = false;
            device.DisabledPersistently = false;
            PendingNotifications++;
        }

        Crash?.Point($"after enable {instanceId}");
        return PnpChange.Done;
    }

    /// <summary>Unplugged: a non-persistent disable is gone with the device node.</summary>
    public void Unplug(string id)
    {
        FakePnpDevice device = this[id];
        device.IsPresent = false;
        device.Disabled = false;
        PendingNotifications++;
    }

    /// <summary>Plugged back in: disabled only if Device Manager disabled it persistently.</summary>
    public void Plug(string id)
    {
        FakePnpDevice device = this[id];
        device.IsPresent = true;
        device.Disabled = device.DisabledPersistently;
        PendingNotifications++;
    }

    /// <summary>The same device plugged into another USB port: a new instance ID, same model and name.</summary>
    public FakePnpDevice MoveToPort(string id, string newId)
    {
        FakePnpDevice old = this[id];
        Unplug(id);
        int last = old.InstanceId.LastIndexOf('\\');
        var moved = new FakePnpDevice
        {
            InstanceId = old.InstanceId[..last] + "\\" + newId,
            Name = old.Name,
            Kind = old.Kind,
            CameraType = old.CameraType,
            IsVirtual = old.IsVirtual,
            IsPresent = true,
            Disabled = old.DisabledPersistently,
            DisabledPersistently = old.DisabledPersistently,
            Original = old.Original,
        };
        _devices.Add(moved);
        PendingNotifications++;
        return moved;
    }

    /// <summary>The user (or another program) enables the device, e.g. in Device Manager.</summary>
    public void SomeoneEnables(string id)
    {
        FakePnpDevice device = this[id];
        device.Disabled = false;
        device.DisabledPersistently = false;
        PendingNotifications++;
    }

    /// <summary>The user disables the device in Device Manager (persistent).</summary>
    public void SomeoneDisables(string id)
    {
        FakePnpDevice device = this[id];
        device.Disabled = true;
        device.DisabledPersistently = true;
        PendingNotifications++;
    }

    /// <summary>The PC restarts: every non-persistent disable is gone.</summary>
    public void Reboot()
    {
        foreach (FakePnpDevice device in _devices)
        {
            device.Disabled = device.IsPresent && device.DisabledPersistently;
        }

        PendingNotifications = 0;
    }

    public int TakeNotifications()
    {
        int pending = PendingNotifications;
        PendingNotifications = 0;
        return pending;
    }

    public int DisableCallsFor(string instanceId) =>
        DisableCalls.Count(c => string.Equals(c, instanceId, StringComparison.OrdinalIgnoreCase));

    public int EnableCallsFor(string instanceId) =>
        EnableCalls.Count(c => string.Equals(c, instanceId, StringComparison.OrdinalIgnoreCase));

    private FakePnpDevice? Find(string instanceId) =>
        _devices.Find(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
}
