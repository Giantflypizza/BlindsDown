using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Shortcuts for tests of the stronger lock (mute, then turn off in Windows).</summary>
internal static class StrongerLockSupport
{
    /// <summary>A world whose settings turn the stronger lock on.</summary>
    public static LockWorld StrongerWorld()
    {
        var world = new LockWorld();
        world.Settings.DisableMicsInWindows = true;
        return world;
    }

    /// <summary>A stronger-locked world with these physical microphones, alerts cleared.</summary>
    public static LockWorld LockedStronger(params string[] ids)
    {
        LockWorld world = StrongerWorld();
        foreach (string id in ids)
        {
            world.Mics.Add(id);
        }

        world.Engine.Initialize();
        world.Engine.Lock();
        world.Pump();
        world.Alerts.Clear();
        return world;
    }

    public static LockAction MuteAction(this LockWorld world, string id) =>
        world.PersistedJournal!.Actions.Single(a => a.Kind == ActionKind.MuteMic && Same(a.TargetId, id));

    public static LockAction? TurnOffAction(this LockWorld world, string id) =>
        world.PersistedJournal!.Actions.SingleOrDefault(a => a.Kind == ActionKind.DisableMicEndpoint && Same(a.TargetId, id));

    /// <summary>
    /// The device changes the engine made since <c>world.Crash.Record()</c>, in order, e.g.
    /// "SetMute(a, True)", "SetEndpointEnabled(a, False)", "SetDefaultMic(a, Console)".
    /// </summary>
    public static List<string> DeviceSteps(this LockWorld world) =>
        world.Crash.Seen
            .Where(p => p.StartsWith("before Set", StringComparison.Ordinal))
            .Select(p => p["before ".Length..])
            .ToList();

    /// <summary>Seeds the pending-restore store as if an earlier stronger lock ended while <paramref name="id"/> was
    /// away: BlindsDown muted it, turned it off, and still owes it both.</summary>
    public static void SeedPendingTurnOn(this LockWorld world, string id)
    {
        world.SeedPendingRestore(id);
        world.Pending.Save(
        [
            .. world.Pending.Load(),
            new LockAction
            {
                Kind = ActionKind.DisableMicEndpoint,
                TargetId = id,
                DisplayName = world.Mics[id].Name,
                Status = ActionStatus.RevertPending,
            },
        ]);
    }

    /// <summary>Whether a microphone is fully back to normal: on and unmuted.</summary>
    public static bool IsOnAndUnmuted(this FakeMic mic) => !mic.IsOff && !mic.Muted;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
