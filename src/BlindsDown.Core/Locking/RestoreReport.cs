using BlindsDown.Core.Devices;

namespace BlindsDown.Core.Locking;

public enum RestoreOutcome
{
    /// <summary>Back exactly as it was before the lock.</summary>
    Exact,

    /// <summary>Not connected right now; it will be restored when it comes back.</summary>
    Pending,

    /// <summary>Different from before, but not because of BlindsDown (another app or the user changed it).</summary>
    ChangedByOthers,

    /// <summary>BlindsDown tried and failed to restore it.</summary>
    Failed,

    /// <summary>A default-device setting differs from before the lock.</summary>
    DefaultChanged,
}

public sealed record RestoreLine(RestoreOutcome Outcome, string DeviceName, string Message, DeviceKind Kind = DeviceKind.Microphone);

/// <summary>What unlocking changed back, compared against the snapshot taken right before locking.</summary>
/// <param name="Lines">One line per device (and per changed default).</param>
/// <param name="DeviceCount">How many devices the lock covered.</param>
/// <param name="CoversCameras">True when the lock also covered cameras or microphone hardware (the helper's
/// part), so the summary says "devices" instead of "microphones".</param>
public sealed record RestoreReport(IReadOnlyList<RestoreLine> Lines, int DeviceCount, bool CoversCameras = false)
{
    public bool AllExact => Lines.All(l => l.Outcome == RestoreOutcome.Exact);

    public int PendingCount => Lines.Count(l => l.Outcome == RestoreOutcome.Pending);

    public int ProblemCount => Lines.Count(l => l.Outcome is RestoreOutcome.Failed or RestoreOutcome.ChangedByOthers or RestoreOutcome.DefaultChanged);

    public string Summary
    {
        get
        {
            if (AllExact)
            {
                string one = CoversCameras ? "device" : "microphone";
                return DeviceCount switch
                {
                    0 => "Nothing needed restoring.",
                    1 => $"1 {one} restored exactly as before.",
                    _ => $"All {DeviceCount} {one}s restored exactly as before.",
                };
            }

            var parts = new List<string>();
            int exact = Lines.Count(l => l.Outcome == RestoreOutcome.Exact);
            if (exact > 0)
            {
                parts.Add($"{exact} restored exactly");
            }

            if (PendingCount > 0)
            {
                parts.Add($"{PendingCount} will be restored when reconnected");
            }

            if (ProblemCount > 0)
            {
                parts.Add(ProblemCount == 1 ? "1 needs attention" : $"{ProblemCount} need attention");
            }

            return string.Join(", ", parts) + ".";
        }
    }

    /// <summary>One report for one unlock of microphones (the app) and cameras (the helper). Either may be null.</summary>
    public static RestoreReport? Combine(RestoreReport? microphones, RestoreReport? devices)
    {
        if (microphones is null || devices is null)
        {
            return microphones ?? devices;
        }

        return new RestoreReport(
            [.. microphones.Lines, .. devices.Lines],
            microphones.DeviceCount + devices.DeviceCount,
            microphones.CoversCameras || devices.CoversCameras || devices.DeviceCount > 0);
    }

    /// <summary>
    /// One line per microphone, whether the lock only muted it or also turned it off in Windows, then a line per
    /// default-microphone role that differs from before the lock. A default the unlock set back is not mentioned.
    /// </summary>
    public static RestoreReport Build(LockJournal journal, AudioSnapshot after)
    {
        var lines = new List<RestoreLine>();
        AudioSnapshot? before = journal.Before;

        // Every microphone once, in the order the lock first touched them.
        List<string> devices = journal.Actions
            .Where(a => a.Kind is ActionKind.MuteMic or ActionKind.DisableMicEndpoint)
            .Select(a => a.TargetId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string id in devices)
        {
            LockAction? mute = Find(journal, ActionKind.MuteMic, id);
            LockAction? turnOff = Find(journal, ActionKind.DisableMicEndpoint, id);
            lines.Add(DeviceLine(mute, turnOff, after.Find(id)));
        }

        if (before is not null)
        {
            AddDefaultLine(lines, "default", before.Defaults.Console, after.Defaults.Console, before, after);

            // Windows sets the console and multimedia roles together ("Default Device" in Sound settings), so the
            // line above covers both, unless the multimedia role differs on its own.
            if (!Same(before.Defaults.Multimedia, before.Defaults.Console) || !Same(after.Defaults.Multimedia, after.Defaults.Console))
            {
                AddDefaultLine(lines, "default multimedia", before.Defaults.Multimedia, after.Defaults.Multimedia, before, after);
            }

            AddDefaultLine(lines, "default communications", before.Defaults.Communications, after.Defaults.Communications, before, after);
        }

        return new RestoreReport(lines, devices.Count);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static LockAction? Find(LockJournal journal, ActionKind kind, string id) =>
        journal.Actions.Find(a => a.Kind == kind && string.Equals(a.TargetId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The line for one microphone: its turn-off (if the stronger lock turned it off) and its mute together.</summary>
    private static RestoreLine DeviceLine(LockAction? mute, LockAction? turnOff, MicEndpoint? now)
    {
        string name = (mute ?? turnOff)!.DisplayName;
        switch (turnOff?.Status)
        {
            case ActionStatus.RevertFailed:
                return new RestoreLine(RestoreOutcome.Failed, name, "Could not be turned back on: " + Reason(turnOff) + ". Turn it on in Windows Sound settings.");
            case ActionStatus.RevertPending:
                return new RestoreLine(
                    RestoreOutcome.Pending,
                    name,
                    mute?.Status == ActionStatus.RevertPending
                        ? "Not connected right now. It will be turned back on and unmuted when it is plugged back in."
                        : "Not connected right now. It will be turned back on when it is plugged back in.");
        }

        // A reverted turn-off without a note was really turned back on (with a note: it was on already).
        bool turnedOn = turnOff is { Status: ActionStatus.Reverted, Note: null };
        if (mute is null)
        {
            return new RestoreLine(RestoreOutcome.Exact, name, turnedOn ? "Turned back on, as before the lock." : "On, as before the lock.");
        }

        // Off in Windows now, although BlindsDown didn't leave it off: someone turned it off during the lock.
        bool offInWindows = now is { State: EndpointState.Disabled };
        switch (mute.Status)
        {
            case ActionStatus.RevertPending:
                return new RestoreLine(
                    RestoreOutcome.Pending,
                    name,
                    turnedOn
                        ? "Turned back on, but not connected right now. It will be unmuted when it is plugged back in."
                        : offInWindows
                            ? "Turned off in Windows right now (not by BlindsDown). It will be unmuted when it is turned back on."
                            : "Not connected right now. It will be unmuted when it is plugged back in.");
            case ActionStatus.RevertFailed:
                return new RestoreLine(
                    RestoreOutcome.Failed,
                    name,
                    (turnedOn ? "Turned back on, but could not be unmuted: " : "Could not be unmuted: ") + Reason(mute) + ". Unmute it in Windows Sound settings.");
        }

        // The journal records the mute state right before BlindsDown first touched the device.
        bool expectedMuted = mute.WasAlreadyLocked;

        if (offInWindows)
        {
            // It was on when the lock took it (only microphones that are on are locked), and BlindsDown owes it nothing.
            return new RestoreLine(RestoreOutcome.ChangedByOthers, name, "Was on before the lock but is turned off in Windows now (changed by another app or by you).");
        }

        if (now is null || !now.IsUsable || now.Muted is null)
        {
            // Away, but BlindsDown owes it nothing (it was already muted, never reached, or restored
            // before it left): nothing will happen when it comes back, so it is not "pending".
            return new RestoreLine(
                RestoreOutcome.Exact,
                name,
                expectedMuted
                    ? "Not connected right now; left muted, as before the lock."
                    : "Not connected right now; nothing left to restore.");
        }

        if (now.Muted == expectedMuted)
        {
            string message = (turnedOn, expectedMuted) switch
            {
                (true, true) => "Turned back on and left muted, as before the lock.",
                (true, false) => "Turned back on and unmuted, as before the lock.",
                (false, true) => "Muted, as before the lock.",
                _ => "Unmuted, as before the lock.",
            };
            return new RestoreLine(RestoreOutcome.Exact, name, message);
        }

        return new RestoreLine(
            RestoreOutcome.ChangedByOthers,
            name,
            expectedMuted
                ? "Was muted before the lock but is unmuted now (changed by another app or by you)."
                : "Was unmuted before the lock but is muted now (changed by another app or by you).");
    }

    private static string Reason(LockAction action) => action.Note?.TrimEnd('.') ?? "unknown error";

    private static void AddDefaultLine(List<RestoreLine> lines, string role, string? beforeId, string? afterId, AudioSnapshot before, AudioSnapshot after)
    {
        if (string.Equals(beforeId, afterId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string beforeName = beforeId is null ? "none" : before.Find(beforeId)?.Name ?? "an unknown microphone";
        string afterName = afterId is null ? "none" : after.Find(afterId)?.Name ?? "an unknown microphone";
        lines.Add(new RestoreLine(RestoreOutcome.DefaultChanged, afterName, $"The {role} microphone changed from \"{beforeName}\" to \"{afterName}\"."));
    }
}
