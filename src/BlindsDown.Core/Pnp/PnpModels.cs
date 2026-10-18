using System.Text.Json.Serialization;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;

namespace BlindsDown.Core.Pnp;

/// <summary>
/// A Plug and Play device the helper service can disable: a camera, or microphone-only audio hardware
/// (a webcam's built-in mic function, a USB microphone). Hardware that also has speakers or headphones is
/// never offered as <see cref="DeviceKind.MicrophoneHardware"/>.
/// </summary>
public sealed record PnpDevice
{
    /// <summary>PnP instance ID, e.g. "USB\VID_046D&amp;PID_0825&amp;MI_00\7&amp;1A2B3C4D&amp;0&amp;0000".</summary>
    public required string InstanceId { get; init; }

    public required string Name { get; init; }

    /// <summary><see cref="DeviceKind.Camera"/> or <see cref="DeviceKind.MicrophoneHardware"/>.</summary>
    public required DeviceKind Kind { get; init; }

    /// <summary>Set for cameras.</summary>
    public CameraType? CameraType { get; init; }

    /// <summary>Connected right now.</summary>
    public bool IsPresent { get; init; }

    /// <summary>Running (driver started).</summary>
    public bool IsStarted { get; init; }

    /// <summary>Disabled (problem code 22), by anyone. For a device that isn't present, from its persistent
    /// config flags.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>Windows says a restart is needed before a change takes effect.</summary>
    public bool NeedsRestart { get; init; }

    public int ProblemCode { get; init; }

    /// <summary>True for software devices (virtual cameras), from the bus/enumerator.</summary>
    public bool IsVirtual { get; init; }

    [JsonIgnore]
    public string StableKey => DeviceIds.StableDeviceKey(Kind == DeviceKind.Camera ? "cam" : "hw", InstanceId, Name);
}

public enum PnpChangeResult
{
    /// <summary>Changed and verified.</summary>
    Done,

    /// <summary>An app has the device open and Windows refused to stop it (query-remove vetoed).</summary>
    Vetoed,

    /// <summary>Windows accepted the change but it only takes effect after a restart.</summary>
    NeedsRestart,

    /// <summary>The device isn't there (unplugged).</summary>
    NotFound,

    /// <summary>Not allowed (the caller isn't an administrator / LocalSystem).</summary>
    AccessDenied,

    Failed,

    /// <summary>
    /// <see cref="IPnpBackend.Enable"/> left the device off on purpose: it is disabled persistently (Device Manager,
    /// Settings, an administrator), which BlindsDown's own disable never is, so whoever did that wants it off.
    /// Nothing was changed, and asking again changes nothing either.
    /// </summary>
    KeptOff,
}

public sealed record PnpChange(PnpChangeResult Result, string? Detail = null)
{
    public static PnpChange Done { get; } = new(PnpChangeResult.Done);

    /// <summary>
    /// Only on a <see cref="PnpChangeResult.Done"/> from <see cref="IPnpBackend.Disable"/>: Windows made the disable
    /// last beyond a restart (BlindsDown never asks for that) and it could not be taken back. Only an
    /// <see cref="IPnpBackend.Enable"/> told so (<c>clearPersistent</c>) turns such a device back on, so the lock
    /// records it (<see cref="DeviceLockJournal.PersistentDisables"/>).
    /// </summary>
    public bool MadePersistent { get; init; }
}

/// <summary>
/// Everything the device lock engine needs from Windows Plug and Play. The real implementation uses
/// CfgMgr32 (CM_Disable_DevNode without CM_DISABLE_PERSIST, so a reboot always re-enables a device if
/// BlindsDown is gone); tests use a fake. Called from one thread.
/// </summary>
public interface IPnpBackend
{
    /// <summary>Cameras and microphone-only audio hardware, present or not.</summary>
    IReadOnlyList<PnpDevice> Enumerate();

    /// <summary>Disables a device until re-enabled or until the next restart. Must verify the result.</summary>
    PnpChange Disable(string instanceId);

    /// <summary>
    /// Re-enables a device that <see cref="Disable"/> disabled. Must verify the result. A device disabled persistently
    /// is left off (<see cref="PnpChangeResult.KeptOff"/>) unless <paramref name="clearPersistent"/> says that this
    /// persistent disable is BlindsDown's own (<see cref="PnpChange.MadePersistent"/>).
    /// </summary>
    PnpChange Enable(string instanceId, bool clearPersistent = false);
}

/// <summary>What the tray app asks the helper to lock. The helper picks the actual devices itself.</summary>
public sealed record DeviceLockRequest
{
    public bool Cameras { get; init; }

    /// <summary>Also disable microphone-only hardware.</summary>
    public bool MicHardware { get; init; }

    public bool IncludeInfrared { get; init; }

    public bool IncludeVirtual { get; init; }

    /// <summary>Devices the user excluded (matched with <see cref="ExclusionRules"/>: by
    /// <see cref="DeviceExclusion.LastKnownId"/> = instance ID, then by stable key).</summary>
    public IReadOnlyList<DeviceExclusion> Exclusions { get; init; } = [];

    /// <summary>
    /// Instance IDs of microphone hardware the app needs left on (behind excluded or default microphones, see
    /// <see cref="LockEngine.HardwareToKeepEnabled"/>). This can only make the helper do less. A camera listed here
    /// (one device that is both a camera and that microphone's hardware) is left on too, but reported as a problem,
    /// so the lock never looks complete while its video is on.
    /// </summary>
    public IReadOnlyList<string> KeepEnabledInstanceIds { get; init; } = [];

    /// <summary>
    /// The same request without missing lists or empty entries. Only a damaged journal or a request built by hand
    /// has them, and the exclusion matching must never trip over a null.
    /// </summary>
    internal DeviceLockRequest Normalized() => this with
    {
        Exclusions = Exclusions is null ? [] : [.. Exclusions.Where(e => e?.StableKey is not null)],
        KeepEnabledInstanceIds = KeepEnabledInstanceIds is null ? [] : [.. KeepEnabledInstanceIds.Where(id => !string.IsNullOrEmpty(id))],
    };

    /// <summary>
    /// Whether <paramref name="other"/> locks exactly the same devices. Compared as sets, ignoring case and the
    /// exclusions' display names, because the record's own equality compares the lists by reference.
    /// </summary>
    internal bool LocksSameAs(DeviceLockRequest other) =>
        Cameras == other.Cameras
        && MicHardware == other.MicHardware
        && IncludeInfrared == other.IncludeInfrared
        && IncludeVirtual == other.IncludeVirtual
        && SameSet(Exclusions.Select(ExclusionKey), other.Exclusions.Select(ExclusionKey))
        && SameSet(KeepEnabledInstanceIds, other.KeepEnabledInstanceIds);

    private static string ExclusionKey(DeviceExclusion exclusion) => exclusion.StableKey + "\n" + exclusion.LastKnownId;

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
}

/// <summary>
/// The helper's lock state, as shown in the tray app. <see cref="Devices"/> lists EVERY camera and microphone
/// hardware device the helper can see, locked or not (Settings shows them while unlocked too), each with its
/// protection: <see cref="DeviceProtection.DeviceDisabled"/>, <see cref="DeviceProtection.Excluded"/>,
/// <see cref="DeviceProtection.NotPresent"/>, <see cref="DeviceProtection.DisabledByUser"/>, or
/// <see cref="DeviceProtection.None"/> (unlocked, not in the request, or failed: then <see cref="DeviceLockInfo.Problem"/>
/// says why, e.g. "In use by an app; BlindsDown keeps trying").
/// </summary>
public sealed record DeviceLockStatus(
    LockState State,
    IReadOnlyList<DeviceLockInfo> Devices,
    IReadOnlyList<string> Problems)
{
    public static DeviceLockStatus Unlocked { get; } = new(LockState.Unlocked, [], []);
}

/// <summary>The crash-safe record of the helper's lock (%ProgramData%\BlindsDown\device-lock-journal.json).</summary>
public sealed class DeviceLockJournal
{
    public int Version { get; set; } = 1;

    public Guid LockId { get; set; }

    public LockPhase Phase { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DeviceLockRequest Request { get; set; } = new();

    /// <summary><see cref="ActionKind.DisableDevice"/> entries. <see cref="LockAction.WasAlreadyLocked"/> means
    /// the device was already disabled before the lock (never re-enabled at unlock).</summary>
    public List<LockAction> Actions { get; set; } = [];

    /// <summary>
    /// The kind of every device in <see cref="Actions"/>, by instance ID, so the restore report can still say
    /// "camera" or "microphone" for a device Windows no longer lists (uninstalled, or gone with its driver).
    /// </summary>
    public Dictionary<string, DeviceKind> DeviceKinds { get; set; } = [];

    /// <summary>
    /// Devices in <see cref="Actions"/> whose disable by this lock Windows made persistent (see
    /// <see cref="PnpChange.MadePersistent"/>), by instance ID. Recorded here, not only in the backend's memory: such a
    /// device is still off after the helper or the PC restarts, and only an enable told that this persistent disable
    /// is BlindsDown's turns it back on.
    /// </summary>
    public List<string> PersistentDisables { get; set; } = [];

    /// <summary>
    /// Drops what a damaged or hand-edited file can contain and the engine must never act on: null entries,
    /// entries without a device, and entries of another kind (the helper only ever disables devices; a
    /// <see cref="ActionKind.MuteMic"/> entry would name an audio endpoint, not a PnP device). A recorded device
    /// kind that isn't a camera or microphone hardware is forgotten too: it would end up in a restore line, and the
    /// tray app refuses a whole answer with a value it doesn't know.
    /// </summary>
    internal void Repair()
    {
        Actions ??= [];
        Actions.RemoveAll(a => a is null || a.Kind != ActionKind.DisableDevice || string.IsNullOrEmpty(a.TargetId));
        foreach (LockAction action in Actions)
        {
            action.DisplayName ??= action.TargetId;
        }

        Request = (Request ?? new DeviceLockRequest()).Normalized();
        DeviceKinds ??= [];
        foreach (string id in DeviceKinds.Where(k => k.Value is not (DeviceKind.Camera or DeviceKind.MicrophoneHardware)).Select(k => k.Key).ToList())
        {
            DeviceKinds.Remove(id);
        }

        PersistentDisables = PersistentDisables is null ? [] : [.. PersistentDisables.Where(id => !string.IsNullOrEmpty(id))];
    }

    /// <summary>Whether Windows made this lock's disable of the device persistent (see <see cref="PersistentDisables"/>).</summary>
    internal bool IsPersistent(string instanceId) =>
        PersistentDisables.Exists(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The kind recorded for a device, or <see cref="DeviceKind.Camera"/> when none was.</summary>
    internal DeviceKind KindOf(string instanceId)
    {
        foreach ((string id, DeviceKind kind) in DeviceKinds)
        {
            if (string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return DeviceKind.Camera;
    }
}

/// <summary>
/// A device an unlock could not turn back on: still off because of BlindsDown. Kept apart from the lock record (an
/// unlock deletes that), so a helper that restarts keeps trying, and never takes the device for one the user turned
/// off.
/// </summary>
public sealed record OwedDevice
{
    public string InstanceId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary><see cref="DeviceKind.Camera"/> or <see cref="DeviceKind.MicrophoneHardware"/>.</summary>
    public DeviceKind Kind { get; init; } = DeviceKind.Camera;

    /// <summary>Windows made BlindsDown's disable persistent (see <see cref="DeviceLockJournal.PersistentDisables"/>):
    /// a restart doesn't turn it back on.</summary>
    public bool Persistent { get; init; }

    /// <summary>
    /// The entries the engine may act on: no empty entries or IDs, and only kinds the tray app knows (a restore line
    /// with any other kind would make it refuse the whole answer).
    /// </summary>
    internal static List<OwedDevice> Repair(IEnumerable<OwedDevice?>? owed) =>
    [
        .. (owed ?? []).OfType<OwedDevice>()
            .Where(o => !string.IsNullOrEmpty(o.InstanceId))
            .Select(o => o with
            {
                Name = string.IsNullOrWhiteSpace(o.Name) ? o.InstanceId : o.Name,
                Kind = o.Kind is DeviceKind.Camera or DeviceKind.MicrophoneHardware ? o.Kind : DeviceKind.Camera,
            }),
    ];
}

public interface IDeviceJournalStore
{
    /// <summary>Null when there is no journal. Throws <see cref="JournalCorruptException"/> when files exist
    /// but none can be read.</summary>
    DeviceLockJournal? Load();

    void Save(DeviceLockJournal journal);

    void Delete();

    /// <summary>
    /// The devices an unlock could not turn back on (see <see cref="OwedDevice"/>). Empty when there are none, or when
    /// the record can't be read: then they are only not retried, and they show as turned off by the user.
    /// </summary>
    IReadOnlyList<OwedDevice> LoadOwedRestores();

    /// <summary>Replaces the record of owed restores; an empty list deletes it.</summary>
    void SaveOwedRestores(IReadOnlyList<OwedDevice> owed);
}
