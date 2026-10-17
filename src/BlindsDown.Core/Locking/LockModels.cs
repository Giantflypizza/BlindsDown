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
    /// <summary>Mute a microphone endpoint (Stage 1, no admin).</summary>
    MuteMic,
}

public enum ActionStatus
{
    /// <summary>
    /// Written to the journal; BlindsDown has not changed the device in this lock. A device whose mute
    /// could not even be read stays here (the error is in the note), so its original state is still
    /// taken from its first good reading.
    /// </summary>
    Planned,

    /// <summary>
    /// The device may already be muted by BlindsDown, but that is not verified yet. Written to the
    /// journal right before the mute, so a crash right after it still gets the mute undone. Also used
    /// for a device an earlier lock muted and could not restore yet (it was away at that unlock).
    /// </summary>
    Applying,

    /// <summary>Applied and verified.</summary>
    Applied,

    /// <summary>The mute was attempted but not verified; the device is NOT protected, yet may be muted.</summary>
    ApplyFailed,

    /// <summary>Undone (or nothing needed undoing).</summary>
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
    /// The device was already in the locked state before we started (e.g. the mic was already muted).
    /// Such devices are guarded while locked but never changed back on unlock.
    /// </summary>
    public bool WasAlreadyLocked { get; set; }

    public ActionStatus Status { get; set; }

    public string? Note { get; set; }

    /// <summary>How often the guard had to re-apply this action while locked.</summary>
    public int GuardCount { get; set; }

    /// <summary>True when the device appeared after the lock started.</summary>
    public bool AddedWhileLocked { get; set; }
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
}

public enum DeviceKind
{
    Microphone,
    Camera,
}

/// <summary>Per-device lock status for the UI.</summary>
public sealed record DeviceLockInfo(
    string Id,
    string Name,
    string StableKey,
    DeviceKind Kind,
    bool IsVirtual,
    bool HasHardwareMute,
    DeviceProtection Protection,
    string? Problem);

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
    /// <summary>Something unmuted a microphone while locked; we muted it again.</summary>
    UnmutedWhileLocked,

    /// <summary>A new microphone appeared while locked; we muted it.</summary>
    NewDeviceLocked,

    /// <summary>A known microphone came back (e.g. reconnected) unmuted; we muted it.</summary>
    DeviceRelocked,

    /// <summary>A device could not be locked.</summary>
    LockFailed,

    /// <summary>Something keeps fighting the lock on a device.</summary>
    Tampering,

    /// <summary>A device that was unplugged at unlock came back and was restored.</summary>
    DeferredRestoreDone,
}

public sealed record EngineAlert(EngineAlertKind Kind, string DeviceName, string Message, bool IsUrgent);

/// <summary>What the app found at startup when a previous lock was still on disk.
/// <c>DevicesChanged</c> counts the devices that may still be muted by BlindsDown.</summary>
public sealed record StartupRecovery(
    LockPhase Phase,
    bool ExitedCleanly,
    DateTimeOffset LockedSince,
    int DevicesChanged);
