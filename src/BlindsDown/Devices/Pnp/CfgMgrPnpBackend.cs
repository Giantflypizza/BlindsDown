using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Devices.Pnp;

/// <summary>
/// [Stage 2 win-pnp] <see cref="IPnpBackend"/> on top of CfgMgr32. Runs inside the helper service (LocalSystem,
/// session 0) for changes; enumeration also works unelevated (the tray app's --snapshot uses it read-only).
///  * Cameras: devices with an interface in KSCATEGORY_VIDEO_CAMERA {E5323777-F976-4F5B-9B55-B94699C46E44} or
///    KSCATEGORY_SENSOR_CAMERA {24E552D7-6523-47F7-A647-D3465BF1F5CA} (CM_Get_Device_Interface_List with
///    CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES, so disabled and missing cameras are listed too). The device is the
///    interface's own devnode, never a composite parent. Classified as Color, Infrared, ColorAndInfrared (one
///    device with both) or Virtual (software enumerated: SWD\, ROOT\, e.g. SWD\VCAMDEVAPI). When unsure, Color:
///    it is locked by default, and a wrong guess shows in Settings and only costs Windows Hello during the lock,
///    never privacy. Cameras with older drivers (setup class Camera or Image, a KSCATEGORY_VIDEO filter that is
///    also KSCATEGORY_CAPTURE) are found too; scanners (class Image, no video filter) are not.
///  * Microphone-only hardware: parents of audio capture endpoints (SWD\MMDEVAPI\{0.0.1.*}) that have no render
///    endpoint ({0.0.0.*}) and are physical (not ROOT/SWD/BTH* enumerated). See
///    <see cref="PnpDeviceRules.SelectMicOnlyHardware"/>.
///  * Disable: CM_Disable_DevNode(CM_DISABLE_UI_NOT_OK), never CM_DISABLE_PERSIST (a reboot always re-enables),
///    then verify with CM_Get_DevNode_Status (DN_HAS_PROBLEM + CM_PROB_DISABLED 22), and that Windows did not make
///    it persistent anyway (CONFIGFLAG_DISABLED, which only CM_DISABLE_PERSIST should set): such a disable is taken
///    back and that device isn't tried again. CR_REMOVE_VETOED: Vetoed. DN_NEED_RESTART: NeedsRestart (and the
///    disable is undone right away: it would only happen at a restart, which re-enables everything anyway).
///    CR_ACCESS_DENIED: AccessDenied. Only devices this class lists itself are ever disabled, whatever ID it is
///    given, so not even a tampered journal can turn off a disk or a network card. A device that is already
///    disabled is not touched: Done with <see cref="AlreadyDisabledDetail"/>. A persistent disable that can't be
///    taken back is Done with <see cref="PnpChange.MadePersistent"/>: the caller records it, because this class
///    forgets it when the helper restarts.
///  * Enable: CM_Enable_DevNode, then verify the problem code is gone. A device that isn't disabled is not touched,
///    and neither is one disabled persistently (CONFIGFLAG_DISABLED): BlindsDown never disables that way, so
///    someone else turned it off (Device Manager, Settings, an administrator), even if the caller believes the
///    disable was BlindsDown's. It stays off: <see cref="PnpChangeResult.KeptOff"/>. The only exception is a
///    persistent disable Windows made of BlindsDown's own (the caller says so with <c>clearPersistent</c>).
/// Not thread-safe: called from one thread (the helper's engine thread).
/// </summary>
internal sealed class CfgMgrPnpBackend : IPnpBackend
{
    /// <summary>The <see cref="PnpChange.Detail"/> of a Done for a device that was already disabled before
    /// <see cref="Disable"/>: nothing was changed.</summary>
    public const string AlreadyDisabledDetail = "It was already turned off.";

    /// <summary>The <see cref="PnpChange.Detail"/> when asked to disable anything that isn't a camera or
    /// microphone-only hardware.</summary>
    public const string NotLockableDetail = "BlindsDown only turns off cameras and microphone-only hardware.";

    /// <summary>The <see cref="PnpChange.Detail"/> of a <see cref="PnpChangeResult.KeptOff"/> from <see cref="Enable"/>
    /// for a device that is disabled persistently, which BlindsDown never does: someone turned it off, and it was left
    /// off. Nothing was changed.</summary>
    public const string KeptOffDetail = "It was turned off in Windows in the meantime, so BlindsDown left it off.";

    /// <summary>The <see cref="PnpChange.Detail"/> when Windows made a disable last beyond a restart.</summary>
    public const string PersistentDetail = "Windows would have kept it off even after a restart, so BlindsDown left it on.";

    private const string Component = "Plug and Play";
    private const string NotConnectedDetail = "It isn't connected.";
    private static readonly IReadOnlySet<string> NoFilters = new HashSet<string>();

    private readonly ICfgMgr _cfgMgr;
    private readonly IAudioEndpointHardwareSource _endpoints;
    private readonly ProblemLog _problems = new(Component);

    /// <summary>Devices whose disable Windows made persistent. They aren't disabled again (each try would switch
    /// the device off and on), and for them <see cref="Enable"/> may clear a persistent disable even when the caller
    /// doesn't say so. Only in memory: the caller's record (<see cref="PnpChange.MadePersistent"/>) is what survives
    /// a restart.</summary>
    private readonly HashSet<string> _persistingDisables = new(StringComparer.OrdinalIgnoreCase);

    public CfgMgrPnpBackend()
        : this(new CfgMgrApi(), new MMDevicesRegistrySource())
    {
    }

    internal CfgMgrPnpBackend(ICfgMgr cfgMgr, IAudioEndpointHardwareSource endpoints)
    {
        _cfgMgr = cfgMgr;
        _endpoints = endpoints;
    }

    /// <summary>Cameras first, then microphone hardware, each by name. Throws
    /// <see cref="DeviceOperationException"/> when Windows can't list devices at all: a partial list would look
    /// like unplugged devices.</summary>
    public IReadOnlyList<PnpDevice> Enumerate() =>
        Discover()
            .Select(ReadDevice)
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public PnpChange Disable(string instanceId)
    {
        if (!PnpDeviceRules.IsValidInstanceId(instanceId))
        {
            return Outcome("disable", instanceId, PnpChangeResult.Failed, "Not a valid device ID.");
        }

        try
        {
            if (!Discover().Exists(candidate => Same(candidate.InstanceId, instanceId)))
            {
                return Outcome("disable", instanceId, PnpChangeResult.Failed, NotLockableDetail);
            }
        }
        catch (DeviceOperationException ex)
        {
            return Outcome("disable", instanceId, PnpChangeResult.Failed, $"Could not check which device it is: {ex.Message}");
        }

        if (LocateConnected(instanceId, "disable", out uint devInst, out NodeState before) is { } notReady)
        {
            return notReady;
        }

        if (before.IsDisabled)
        {
            return Succeeded("disable", instanceId, new PnpChange(PnpChangeResult.Done, AlreadyDisabledDetail), $"{instanceId} was already disabled; left as it is.");
        }

        if (_persistingDisables.Contains(instanceId))
        {
            return Outcome("disable", instanceId, PnpChangeResult.Failed, PersistentDetail);
        }

        bool persistentBefore = IsPersistentlyDisabled(devInst);
        uint cr = _cfgMgr.DisableDevNode(devInst, CmDisableUiNotOk);
        NodeState after = ReadState(devInst);
        if (after.IsDisabled && !after.IsStarted)
        {
            if (!persistentBefore && IsPersistentlyDisabled(devInst))
            {
                // Contrary to the documentation (only CM_DISABLE_PERSIST sets CONFIGFLAG_DISABLED), the disable
                // would outlive a restart, and BlindsDown promises that a restart brings every device back even if
                // BlindsDown is gone or broken. Take it back; if that fails, it is BlindsDown's to re-enable at unlock.
                _persistingDisables.Add(instanceId);
                return Undo(devInst, instanceId, persistentBefore)
                    ? Outcome("disable", instanceId, PnpChangeResult.Failed, PersistentDetail)
                    : Succeeded("disable", instanceId, PnpChange.Done with { MadePersistent = true }, $"disabled {instanceId}, and Windows keeps it off even after a restart until it is re-enabled.");
            }

            return Succeeded("disable", instanceId, PnpChange.Done, $"disabled {instanceId} (until it is re-enabled or Windows restarts).");
        }

        // Windows took the disable but can only carry it out at a restart. A restart re-enables everything anyway
        // (the disable isn't persistent), so all it could do is leave the device in a half state: undo it.
        if (after.IsDisabled || (after.NeedsRestart && !before.NeedsRestart) || cr == CrNeedRestart)
        {
            return Undo(devInst, instanceId, persistentBefore)
                ? Outcome("disable", instanceId, PnpChangeResult.NeedsRestart, "Windows could only turn it off after a restart, so BlindsDown left it on.")
                : Outcome("disable", instanceId, PnpChangeResult.Failed, "Windows could only turn it off after a restart, and BlindsDown could not take that back. A restart turns it back on.");
        }

        if (cr == CrRemoveVetoed)
        {
            return Outcome("disable", instanceId, PnpChangeResult.Vetoed, "An app is using it, and Windows won't turn it off while it is in use.");
        }

        if (cr == CrAccessDenied)
        {
            return Outcome("disable", instanceId, PnpChangeResult.AccessDenied, "Turning devices off needs administrator rights.");
        }

        if (CfgMgrErrors.IsGone(cr) || CfgMgrErrors.IsGone(after.Cr))
        {
            return Outcome("disable", instanceId, PnpChangeResult.NotFound, NotConnectedDetail);
        }

        if (cr == CrNotDisableable)
        {
            return Outcome("disable", instanceId, PnpChangeResult.Failed, "Windows says this device can't be turned off.");
        }

        if (cr == CrSuccess)
        {
            // Windows said yes, yet the device isn't verifiably off. Whatever happened, make sure nothing is left half done.
            return Undo(devInst, instanceId, persistentBefore)
                ? Outcome("disable", instanceId, PnpChangeResult.Failed, "Windows reported success, but the device is still on.")
                : Outcome("disable", instanceId, PnpChangeResult.Failed, "Windows reported success, but BlindsDown could not check the device afterwards. A restart turns it back on.");
        }

        return Outcome("disable", instanceId, PnpChangeResult.Failed, $"Windows refused: {CfgMgrErrors.Describe(cr)}.");
    }

    public PnpChange Enable(string instanceId, bool clearPersistent = false)
    {
        if (!PnpDeviceRules.IsValidInstanceId(instanceId))
        {
            return Outcome("enable", instanceId, PnpChangeResult.Failed, "Not a valid device ID.");
        }

        if (LocateConnected(instanceId, "enable", out uint devInst, out NodeState before) is { } notReady)
        {
            return notReady;
        }

        if (!before.IsDisabled)
        {
            return Succeeded("enable", instanceId, PnpChange.Done, $"{instanceId} is already on; left as it is.");
        }

        if (IsPersistentlyDisabled(devInst) && !clearPersistent && !_persistingDisables.Contains(instanceId))
        {
            // BlindsDown's disables never set CONFIGFLAG_DISABLED (Disable checks), so someone turned this device off
            // for good while it was locked, or the caller is wrong about whose disable this is. Either way: theirs.
            return Succeeded("enable", instanceId, new PnpChange(PnpChangeResult.KeptOff, KeptOffDetail), $"{instanceId} is turned off persistently (not by BlindsDown); left off.");
        }

        uint cr = _cfgMgr.EnableDevNode(devInst);
        NodeState after = ReadState(devInst);
        if (after.Exists && !after.IsDisabled)
        {
            return cr == CrNeedRestart || (after.NeedsRestart && !before.NeedsRestart)
                ? Outcome("enable", instanceId, PnpChangeResult.NeedsRestart, "Windows turns it back on at the next restart.")
                : Succeeded("enable", instanceId, PnpChange.Done, $"re-enabled {instanceId}.");
        }

        if (cr == CrAccessDenied)
        {
            return Outcome("enable", instanceId, PnpChangeResult.AccessDenied, "Turning devices back on needs administrator rights.");
        }

        if (CfgMgrErrors.IsGone(cr) || CfgMgrErrors.IsGone(after.Cr))
        {
            return Outcome("enable", instanceId, PnpChangeResult.NotFound, NotConnectedDetail);
        }

        return cr == CrSuccess
            ? Outcome("enable", instanceId, PnpChangeResult.Failed, "Windows reported success, but the device is still off.")
            : Outcome("enable", instanceId, PnpChangeResult.Failed, $"Windows refused: {CfgMgrErrors.Describe(cr)}.");
    }

    /// <summary>Every camera and piece of microphone-only hardware, with its devnode (found even when unplugged).</summary>
    private List<Candidate> Discover()
    {
        var candidates = new List<Candidate>();
        foreach ((string id, CameraType type) in FindCameras())
        {
            if (LocateAny(id) is { } devInst)
            {
                candidates.Add(new Candidate(CanonicalId(devInst, id), DeviceKind.Camera, type, devInst));
            }
        }

        foreach (string id in PnpDeviceRules.SelectMicOnlyHardware(_endpoints.Read(), ParentOf))
        {
            // One devnode with both video and audio is a camera: listing it twice would give two IDs for one device.
            if (!candidates.Exists(candidate => Same(candidate.InstanceId, id)) && LocateAny(id) is { } devInst)
            {
                candidates.Add(new Candidate(id, DeviceKind.MicrophoneHardware, null, devInst));
            }
        }

        return candidates;
    }

    private Dictionary<string, CameraType> FindCameras()
    {
        Dictionary<string, HashSet<string>> color = FiltersByDevice(PnpKeys.VideoCameraInterface);
        Dictionary<string, HashSet<string>> sensor = FiltersByDevice(PnpKeys.SensorCameraInterface);
        var cameras = new Dictionary<string, CameraType>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in color.Keys.Concat(sensor.Keys))
        {
            cameras[id] = PnpDeviceRules.ClassifyCamera(id, color.GetValueOrDefault(id) ?? NoFilters, sensor.GetValueOrDefault(id) ?? NoFilters);
        }

        foreach ((string id, HashSet<string> filters) in FindOlderCameras(cameras))
        {
            cameras[id] = PnpDeviceRules.ClassifyCamera(id, filters, NoFilters);
        }

        return cameras;
    }

    /// <summary>
    /// Cameras whose drivers predate KSCATEGORY_VIDEO_CAMERA: setup class Camera or Image with a KSCATEGORY_VIDEO
    /// filter that is also a KSCATEGORY_CAPTURE filter. Scanners are class Image too but have no video filter;
    /// TV tuners and HDMI capture cards have one but are class Media, and are left alone.
    /// </summary>
    private Dictionary<string, HashSet<string>> FindOlderCameras(Dictionary<string, CameraType> known)
    {
        var captureFilters = ListInterfaces(PnpKeys.CaptureInterface).Select(PnpDeviceRules.FilterKey).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ListInterfaces(PnpKeys.VideoInterface))
        {
            string filter = PnpDeviceRules.FilterKey(path);
            if (!captureFilters.Contains(filter) || InstanceIdOf(path) is not { } id || known.ContainsKey(id) || rejected.Contains(id))
            {
                continue;
            }

            if (found.TryGetValue(id, out HashSet<string>? filters))
            {
                filters.Add(filter);
            }
            else if (LocateAny(id) is { } devInst && _cfgMgr.GetGuid(devInst, PnpKeys.ClassGuid) is { } setupClass
                && (setupClass == PnpKeys.CameraClass || setupClass == PnpKeys.ImageClass))
            {
                found[id] = new HashSet<string>(StringComparer.Ordinal) { filter };
            }
            else
            {
                rejected.Add(id);
            }
        }

        return found;
    }

    /// <summary>For each device: the filters (<see cref="PnpDeviceRules.FilterKey"/>) it registers in the class.</summary>
    private Dictionary<string, HashSet<string>> FiltersByDevice(Guid interfaceClass)
    {
        var filters = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ListInterfaces(interfaceClass))
        {
            if (InstanceIdOf(path) is not { } id)
            {
                continue;
            }

            if (!filters.TryGetValue(id, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                filters[id] = set;
            }

            set.Add(PnpDeviceRules.FilterKey(path));
        }

        return filters;
    }

    private IReadOnlyList<string> ListInterfaces(Guid interfaceClass)
    {
        uint cr = _cfgMgr.GetDeviceInterfaces(interfaceClass, out IReadOnlyList<string> paths);
        return cr == CrSuccess
            ? paths
            : throw new DeviceOperationException($"Windows could not list the devices of interface class {interfaceClass:B}: {CfgMgrErrors.Describe(cr)}");
    }

    private string? InstanceIdOf(string interfacePath)
    {
        string? id = _cfgMgr.GetInterfaceInstanceId(interfacePath);
        if (PnpDeviceRules.IsValidInstanceId(id))
        {
            return id;
        }

        _problems.Report("interface " + interfacePath, $"Could not tell which device the interface {interfacePath} belongs to; skipped it.");
        return null;
    }

    /// <summary>The devnode of a device that may be unplugged, or null (logged once) when Windows doesn't know it.</summary>
    private uint? LocateAny(string instanceId)
    {
        uint cr = _cfgMgr.LocateDevNode(instanceId, includeNotPresent: true, out uint devInst);
        if (cr == CrSuccess)
        {
            _problems.Resolve("locate " + instanceId);
            return devInst;
        }

        _problems.Report("locate " + instanceId, $"Windows doesn't know the device {instanceId} ({CfgMgrErrors.Describe(cr)}); skipped it.");
        return null;
    }

    /// <summary>
    /// The devnode's own spelling of its ID. An interface reports its device's ID in the case of its symbolic link
    /// ("...\7&amp;1a2b3c4d&amp;0&amp;0000"), while Device Manager, Core Audio and the journal use the devnode's
    /// ("...\7&amp;1A2B3C4D&amp;0&amp;0000"). IDs are case-insensitive, but one spelling everywhere avoids surprises.
    /// </summary>
    private string CanonicalId(uint devInst, string instanceId) =>
        _cfgMgr.GetString(devInst, PnpKeys.InstanceId) is { } own && Same(own, instanceId) ? own : instanceId;

    private string? ParentOf(string instanceId) =>
        _cfgMgr.LocateDevNode(instanceId, includeNotPresent: true, out uint devInst) == CrSuccess
            ? _cfgMgr.GetString(devInst, PnpKeys.Parent)
            : null;

    /// <summary>Finds a CONNECTED device and reads its state; returns the outcome to report when that fails.</summary>
    private PnpChange? LocateConnected(string instanceId, string action, out uint devInst, out NodeState state)
    {
        state = default;
        uint cr = _cfgMgr.LocateDevNode(instanceId, includeNotPresent: false, out devInst);
        if (cr == CrSuccess)
        {
            state = ReadState(devInst);
            cr = state.Cr;
        }

        if (cr == CrSuccess)
        {
            return null;
        }

        return CfgMgrErrors.IsGone(cr)
            ? Outcome(action, instanceId, PnpChangeResult.NotFound, NotConnectedDetail)
            : Outcome(action, instanceId, PnpChangeResult.Failed, $"Could not read the device's state: {CfgMgrErrors.Describe(cr)}.");
    }

    private PnpDevice ReadDevice(Candidate candidate)
    {
        uint devInst = candidate.DevInst;
        NodeState state = ReadState(devInst);
        bool? isPresent = _cfgMgr.GetBoolean(devInst, PnpKeys.IsPresent);

        // A connected device has a live status; an unplugged one (a phantom devnode) has none. When the status
        // fails for another reason, the property is the best answer there is.
        bool present = state.Exists ? isPresent != false : !CfgMgrErrors.IsGone(state.Cr) && isPresent == true;
        if (!state.Exists && !CfgMgrErrors.IsGone(state.Cr))
        {
            _problems.Report("status " + candidate.InstanceId, $"Could not read the state of {candidate.InstanceId}: {CfgMgrErrors.Describe(state.Cr)}.");
        }

        return new PnpDevice
        {
            InstanceId = candidate.InstanceId,
            Name = PnpDeviceRules.ChooseName(
                _cfgMgr.GetString(devInst, PnpKeys.FriendlyName),
                _cfgMgr.GetString(devInst, PnpKeys.BusReportedDeviceDesc),
                _cfgMgr.GetString(devInst, PnpKeys.DeviceDesc),
                candidate.Kind == DeviceKind.Camera ? "Camera" : "Microphone"),
            Kind = candidate.Kind,
            CameraType = candidate.CameraType,
            IsPresent = present,
            IsStarted = present && state.IsStarted,

            // Windows keeps no live state for an unplugged device; only a persistent disable (Device Manager,
            // Settings) is recorded, in its config flags.
            IsDisabled = present ? state.IsDisabled : IsPersistentlyDisabled(devInst),
            NeedsRestart = present && state.NeedsRestart,
            ProblemCode = present ? (int)state.Problem : 0,
            IsVirtual = DeviceIds.IsVirtualHardware(candidate.InstanceId),
        };
    }

    private NodeState ReadState(uint devInst)
    {
        uint cr = _cfgMgr.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CrSuccess)
        {
            return new NodeState(cr, IsDisabled: false, IsStarted: false, NeedsRestart: false, Problem: 0);
        }

        uint problemCode = (status & DnHasProblem) != 0 ? problem : 0;
        return new NodeState(
            cr,
            IsDisabled: problemCode == CmProbDisabled,
            IsStarted: (status & DnStarted) != 0,
            NeedsRestart: (status & DnNeedRestart) != 0 || problemCode == CmProbNeedRestart,
            Problem: problemCode);
    }

    /// <summary>
    /// Takes back a disable that didn't verifiably happen, or happened in a way BlindsDown never leaves behind.
    /// Enabling a device that is on changes nothing. True when the device is verifiably not disabled afterwards,
    /// and not disabled persistently unless it already was (<paramref name="persistentBefore"/>).
    /// </summary>
    private bool Undo(uint devInst, string instanceId, bool persistentBefore)
    {
        uint cr = _cfgMgr.EnableDevNode(devInst);
        NodeState state = ReadState(devInst);
        if (state.Exists && !state.IsDisabled && (persistentBefore || !IsPersistentlyDisabled(devInst)))
        {
            Log.Info($"{Component}: took back the disable of {instanceId}.");
            return true;
        }

        string now = state.Exists ? $"problem {state.Problem}" : CfgMgrErrors.Describe(state.Cr);
        Log.Error($"{Component}: could not take back the disable of {instanceId} (enable: {CfgMgrErrors.Describe(cr)}; now: {now}).");
        return false;
    }

    /// <summary>CONFIGFLAG_DISABLED: turned off until someone turns it back on, restarts included. Only a persistent
    /// disable (Device Manager, Settings, CM_DISABLE_PERSIST) sets it; BlindsDown never does.</summary>
    private bool IsPersistentlyDisabled(uint devInst) =>
        ((_cfgMgr.GetUInt32(devInst, PnpKeys.ConfigFlags) ?? 0) & ConfigFlagDisabled) != 0;

    /// <summary>A change that didn't happen. Logged once per device and outcome: the helper retries a vetoed
    /// disable every few seconds while an app holds the camera, and the log must not fill up with it.</summary>
    private PnpChange Outcome(string action, string instanceId, PnpChangeResult result, string detail)
    {
        _problems.Report($"{action} {instanceId}", $"could not {action} {instanceId}: {result}: {detail}");
        return new PnpChange(result, detail);
    }

    /// <summary>A change that happened (or wasn't needed): logged, and a repeated failure before it is closed.</summary>
    private PnpChange Succeeded(string action, string instanceId, PnpChange change, string message)
    {
        _problems.Resolve($"{action} {instanceId}");
        Log.Info($"{Component}: {message}");
        return change;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed record Candidate(string InstanceId, DeviceKind Kind, CameraType? CameraType, uint DevInst);

    /// <summary>A connected device's live state. <see cref="Exists"/> is false when the status can't be read.</summary>
    private readonly record struct NodeState(uint Cr, bool IsDisabled, bool IsStarted, bool NeedsRestart, uint Problem)
    {
        public bool Exists => Cr == CrSuccess;
    }
}
