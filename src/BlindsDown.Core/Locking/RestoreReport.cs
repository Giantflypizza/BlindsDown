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

public sealed record RestoreLine(RestoreOutcome Outcome, string DeviceName, string Message);

/// <summary>What unlocking changed back, compared against the snapshot taken right before locking.</summary>
public sealed record RestoreReport(IReadOnlyList<RestoreLine> Lines, int DeviceCount)
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
                return DeviceCount switch
                {
                    0 => "Nothing needed restoring.",
                    1 => "1 microphone restored exactly as before.",
                    _ => $"All {DeviceCount} microphones restored exactly as before.",
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

    public static RestoreReport Build(LockJournal journal, AudioSnapshot after)
    {
        var lines = new List<RestoreLine>();
        AudioSnapshot? before = journal.Before;

        foreach (LockAction action in journal.Actions)
        {
            if (action.Kind != ActionKind.MuteMic)
            {
                continue;
            }

            MicEndpoint? now = after.Find(action.TargetId);
            string name = action.DisplayName;

            switch (action.Status)
            {
                case ActionStatus.RevertPending:
                    lines.Add(new RestoreLine(RestoreOutcome.Pending, name, "Not connected right now. It will be unmuted when it is plugged back in."));
                    continue;
                case ActionStatus.RevertFailed:
                    lines.Add(new RestoreLine(RestoreOutcome.Failed, name, "Could not be unmuted: " + (action.Note?.TrimEnd('.') ?? "unknown error") + ". Unmute it in Windows Sound settings."));
                    continue;
            }

            // The journal records the mute state right before BlindsDown first touched the device.
            bool expectedMuted = action.WasAlreadyLocked;

            if (now is null || !now.IsUsable || now.Muted is null)
            {
                // Away, but BlindsDown owes it nothing (it was already muted, never reached, or restored
                // before it left): nothing will happen when it comes back, so it is not "pending".
                lines.Add(new RestoreLine(
                    RestoreOutcome.Exact,
                    name,
                    expectedMuted
                        ? "Not connected right now; left muted, as before the lock."
                        : "Not connected right now; nothing left to restore."));
            }
            else if (now.Muted == expectedMuted)
            {
                lines.Add(new RestoreLine(RestoreOutcome.Exact, name, expectedMuted ? "Muted, as before the lock." : "Unmuted, as before the lock."));
            }
            else
            {
                lines.Add(new RestoreLine(
                    RestoreOutcome.ChangedByOthers,
                    name,
                    expectedMuted
                        ? "Was muted before the lock but is unmuted now (changed by another app or by you)."
                        : "Was unmuted before the lock but is muted now (changed by another app or by you)."));
            }
        }

        if (before is not null)
        {
            AddDefaultLine(lines, "default", before.Defaults.Console, after.Defaults.Console, before, after);
            AddDefaultLine(lines, "default communications", before.Defaults.Communications, after.Defaults.Communications, before, after);
        }

        int deviceCount = journal.Actions.Count(a => a.Kind == ActionKind.MuteMic);
        return new RestoreReport(lines, deviceCount);
    }

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
