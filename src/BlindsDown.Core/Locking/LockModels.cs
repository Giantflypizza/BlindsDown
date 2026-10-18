using BlindsDown.Core.Devices;

namespace BlindsDown.Core.Locking;

/// <summary>Where a lock is in its life. Saved in the journal so a crash can be recovered.</summary>
public enum LockPhase
{
    Locking,
    Locked,
    Unlocking,
}

/// <summary>What one journal entry does to one device.</summary>
public enum ActionKind
{
    /// <summary>Mute a microphone endpoint (no admin).</summary>
    MuteMic,

    /// <summary>Turn a microphone off in Windows: the Sound settings "Don't allow" switch. Applied after the
    /// mute and undone before the unmute.</summary>
    DisableMicEndpoint,

    /// <summary>Disable a PnP device (a camera, or a microphone-only USB/webcam audio function) at the
    /// device level. Done by the helper service, which has administrator rights.</summary>
    DisableDevice,
}

public enum ActionStatus
{
    /// <summary>
    /// Written to the journal; BlindsDown has not changed the device in this lock. A device whose mute
    /// could not even be read stays here (the error is in the note), so its original state is still
    /// taken from its first good reading. A turn-off that stays here was decided against: the lock keeps
    /// that (default) microphone on, and the note says why.
    /// </summary>
    Planned,

    /// <summary>
    /// The device may already be muted (or turned off) by BlindsDown, but that is not verified yet. Written
    /// to the journal right before the change, so a crash right after it still gets the change undone. Also
    /// used for a device an earlier lock muted and could not restore yet (it was away at that unlock).
    /// </summary>
    Applying,

    /// <summary>Applied and verified.</summary>
    Applied,

    /// <summary>The change was attempted but not verified. For a mute: the device is NOT protected, yet may be
    /// muted. For a turn-off: the mute still protects it, and it may or may not be off.</summary>
    ApplyFailed,

    /// <summary>Undone (or nothing needed undoing; the note then says why).</summary>
    Reverted,

    /// <summary>Device unavailable at unlock; will be undone when it comes back.</summary>
    RevertPending,

    /// <summary>Undo failed; the user must be told.</summary>
    RevertFailed,
}

/// <summary>One journaled change to one device. Written to disk BEFORE the change is made.</summary>
public sealed class LockAction
{
    public ActionKind Kind { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The device was already in the locked state before the lock started (e.g. the mic was already muted).
    /// Such devices are guarded while locked but never changed back on unlock.
    /// </summary>
    public bool WasAlreadyLocked { get; set; }

    public ActionStatus Status { get; set; }

    public string? Note { get; set; }

    /// <summary>How often the guard had to re-apply this action while locked.</summary>
    public int GuardCount { get; set; }

    /// <summary>True when the device appeared after the lock started.</summary>
    public bool AddedWhileLocked { get; set; }

    /// <summary>
    /// On a pending restore (the device was away at unlock): the Windows default-microphone roles the lock moved
    /// away from this microphone, each holding the default it had at unlock. When the microphone comes back, every
    /// role that still has that default is made this microphone's again; a role someone changed since is left
    /// alone. A lock that takes such a restore over (the microphone is usable again when it locks) carries the
    /// roles on its own entry and settles them at its unlock. Null for most entries.
    /// </summary>
    public DefaultMics? DefaultsToRestore { get; set; }

    /// <summary>A separate copy, so a change to one doesn't show in the other.</summary>
    internal LockAction Copy() => (LockAction)MemberwiseClone();
}

/// <summary>The crash-safe record of an active lock (%LocalAppData%\BlindsDown\lock-journal.json).</summary>
public sealed class LockJournal
{
    public int Version { get; set; } = 1;

    public Guid LockId { get; set; }

    public LockPhase Phase { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Set when the app exited normally (logoff, shutdown, "Exit") while locked.</summary>
    public bool ExitedCleanlyWhileLocked { get; set; }

    /// <summary>Device state just before locking; used for the restore report.</summary>
    public AudioSnapshot? Before { get; set; }

    /// <summary>
    /// The Windows default microphones once the lock settled. Turning a default microphone off in Windows makes
    /// Windows pick another default; at unlock, a role whose default is still this one (nobody chose another
    /// since) is set back to its default from <see cref="Before"/>. So is a role that is null here: it had no
    /// default at all (every microphone was off), so the one it has at unlock is simply the first microphone
    /// Windows saw come back on. Null for locks that changed no default.
    /// </summary>
    public DefaultMics? DefaultsWhileLocked { get; set; }

    /// <summary>
    /// The stronger lock: besides muting, this lock turns physical microphones off in Windows. Copied from
    /// <see cref="BlindsDown.Core.Settings.AppSettings.DisableMicsInWindows"/> when the lock starts, so
    /// changing the setting during a lock only affects the next one (and a lock resumed after a crash keeps
    /// its own choice).
    /// </summary>
    public bool DisableMicEndpoints { get; set; }

    /// <summary>Windows refused to turn microphones off for this lock (it wants administrator rights, or doesn't
    /// support it): the lock only mutes from then on, and says so once.</summary>
    public bool EndpointDisableUnavailable { get; set; }

    /// <summary>
    /// The helper's part of this lock ran (<see cref="LockEngine.NoteDefaultsSettled"/>), so microphone hardware may
    /// be disabled. Windows moves a default away from such a microphone only once it has disappeared, possibly a
    /// moment after the helper answered: while this is set, a default that moves away from the default last seen,
    /// to which the lock's record still points, and away from a microphone that is no longer there counts as moved
    /// by the lock, and unlock puts it back. A default someone picks moves away from a microphone that is still
    /// there, so it never counts.
    /// </summary>
    public bool HelperMayMoveDefaults { get; set; }

    public List<LockAction> Actions { get; set; } = [];
}

public enum LockState
{
    Unlocked,
    Locking,
    Locked,

    /// <summary>Locked, but at least one device could not be protected.</summary>
    PartiallyLocked,
    Unlocking,
}

public enum DeviceProtection
{
    /// <summary>Not protected (unlocked, or the lock failed for this device).</summary>
    None,

    /// <summary>The user excluded this device.</summary>
    Excluded,

    /// <summary>Muted, and the device mutes in hardware.</summary>
    HardwareMute,

    /// <summary>Muted in software. Exclusive-mode apps can bypass this.</summary>
    SoftwareMute,

    /// <summary>Not connected right now; it will be locked when it appears.</summary>
    NotPresent,

    /// <summary>Disabled before the lock (e.g. by the user); left alone.</summary>
    DisabledByUser,

    /// <summary>Muted and also turned off in Windows (Sound settings "Don't allow"): apps can't open it.</summary>
    EndpointDisabled,

    /// <summary>Disabled at the device level by the helper service: nothing on the PC can use it.</summary>
    DeviceDisabled,

    /// <summary>A camera that can only be locked once the optional helper is installed.</summary>
    NeedsHelper,
}

public enum DeviceKind
{
    /// <summary>A microphone endpoint (what apps see in their microphone list).</summary>
    Microphone,
    Camera,

    /// <summary>The hardware behind microphones that has no speakers, e.g. a webcam's built-in mic or a USB mic.</summary>
    MicrophoneHardware,
}

/// <summary>What kind of camera a device is. Infrared (Windows Hello) and virtual cameras are left out of the
/// lock by default.</summary>
public enum CameraType
{
    /// <summary>A normal colour webcam.</summary>
    Color,

    /// <summary>An infrared camera, usually for Windows Hello face sign-in.</summary>
    Infrared,

    /// <summary>One device with colour and infrared streams: locking it also turns off Windows Hello face sign-in.</summary>
    ColorAndInfrared,

    /// <summary>A software camera (OBS, Phone Link, NVIDIA Broadcast...). Most pass on a real camera; Phone Link shows a phone's.</summary>
    Virtual,
}

/// <summary>Per-device lock status for the UI.</summary>
/// <param name="Problem">Why the device is not protected; null when it is.</param>
/// <param name="Detail">A remark about a protected device that isn't a problem, e.g. why a default microphone is
/// only muted during a stronger lock: "Kept on so Windows doesn't switch apps to "Headset (Bluetooth)".".</param>
public sealed record DeviceLockInfo(
    string Id,
    string Name,
    string StableKey,
    DeviceKind Kind,
    bool IsVirtual,
    bool HasHardwareMute,
    DeviceProtection Protection,
    string? Problem,
    CameraType? Camera = null,
    string? Detail = null);

/// <summary>Everything the UI needs to show the lock state.</summary>
public sealed record LockStatusInfo(
    LockState State,
    IReadOnlyList<DeviceLockInfo> Devices,
    IReadOnlyList<string> Problems)
{
    public static LockStatusInfo Unlocked { get; } = new(LockState.Unlocked, [], []);

    public bool IsLockedOrPartly => State is LockState.Locked or LockState.PartiallyLocked;
}

public enum EngineAlertKind
{
    /// <summary>Something unmuted a microphone while locked; BlindsDown muted it again.</summary>
    UnmutedWhileLocked,

    /// <summary>A new microphone appeared while locked; BlindsDown muted it.</summary>
    NewDeviceLocked,

    /// <summary>A known microphone came back (e.g. reconnected) unmuted; BlindsDown muted it.</summary>
    DeviceRelocked,

    /// <summary>A device could not be locked.</summary>
    LockFailed,

    /// <summary>Something keeps fighting the lock on a device.</summary>
    Tampering,

    /// <summary>A device that was unplugged at unlock came back and was restored.</summary>
    DeferredRestoreDone,

    /// <summary>Something turned a microphone or camera back on while locked; BlindsDown turned it off again.</summary>
    ReEnabledWhileLocked,

    /// <summary>Windows refused the stronger "turn off in Windows" microphone lock; microphones are muted instead.</summary>
    StrongerLockUnavailable,

    /// <summary>A device is in use and Windows won't disable it right now; BlindsDown keeps trying.</summary>
    DeviceBusy,
}

public sealed record EngineAlert(EngineAlertKind Kind, string DeviceName, string Message, bool IsUrgent);

/// <summary>What the app found at startup when a previous lock was still on disk.
/// <c>DevicesChanged</c> counts the devices (not the changes) that may still be muted or turned off by BlindsDown.</summary>
public sealed record StartupRecovery(
    LockPhase Phase,
    bool ExitedCleanly,
    DateTimeOffset LockedSince,
    int DevicesChanged);
