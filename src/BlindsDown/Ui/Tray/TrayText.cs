using System.Text;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Usage;

namespace BlindsDown.Ui.Tray;

/// <summary>Wording for the tray tooltip and menu. Pure functions so the limits can be unit-tested.</summary>
internal static class TrayText
{
    /// <summary>NOTIFYICONDATA.szTip holds 128 UTF-16 units including the terminator; WinForms throws above 127.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Menu lines longer than this make the menu uncomfortably wide.</summary>
    public const int MaxMenuLineLength = 64;

    public static string Headline(LockState state) => state switch
    {
        LockState.Locked => "Microphones locked",
        LockState.PartiallyLocked => "Microphones partially locked",
        LockState.Locking => "Locking microphones…",
        LockState.Unlocking => "Unlocking microphones…",
        _ => "Microphones unlocked",
    };

    /// <summary>Text of the lock/unlock menu item (the hotkey is shown separately, right-aligned).</summary>
    public static string ToggleLabel(LockState state) => state switch
    {
        LockState.Locked or LockState.PartiallyLocked => "&Unlock microphones",
        LockState.Locking => "Locking…",
        LockState.Unlocking => "Unlocking…",
        _ => "&Lock microphones",
    };

    /// <summary>
    /// "BlindsDown: microphones locked" plus one line per device kind in use right now, e.g.
    /// "Mic in use: Discord (via SteelSeries Sonar), Zoom". Always at most <see cref="MaxTooltipLength"/>.
    /// </summary>
    public static string Tooltip(LockStatusInfo status, IReadOnlyList<UsageSession> active)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(active);

        string first = "BlindsDown: " + LowerFirst(Headline(status.State));
        if (status.State == LockState.PartiallyLocked && status.Problems.Count > 0)
        {
            first += status.Problems.Count == 1 ? " (1 problem)" : $" ({status.Problems.Count} problems)";
        }

        var sb = new StringBuilder(Truncate(first, MaxTooltipLength));
        foreach (string line in UsageLines(active, "Mic in use: ", "Camera in use: "))
        {
            int room = MaxTooltipLength - sb.Length - 1; // 1 for the newline
            if (room < 12)
            {
                break; // not enough space for anything readable
            }

            sb.Append('\n').Append(Truncate(line, room));
        }

        return sb.ToString();
    }

    /// <summary>Menu lines describing who uses the mic/camera now ("Microphone in use by Discord").</summary>
    public static IReadOnlyList<string> UsageMenuLines(IReadOnlyList<UsageSession> active) =>
        [.. UsageLines(active, "Microphone in use by ", "Camera in use by ").Select(l => Truncate(l, MaxMenuLineLength))];

    /// <summary>The first problem of a partial lock, with a count of the others.</summary>
    public static string? ProblemMenuLine(LockStatusInfo status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State != LockState.PartiallyLocked)
        {
            return null;
        }

        if (status.Problems.Count == 0)
        {
            return "Some microphones are not protected";
        }

        string suffix = status.Problems.Count > 1 ? $" (+{status.Problems.Count - 1} more)" : string.Empty;
        return Truncate(status.Problems[0], MaxMenuLineLength - suffix.Length) + suffix;
    }

    /// <summary>Shortens <paramref name="text"/> to at most <paramref name="max"/> characters, ending in an ellipsis.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        if (max == 1)
        {
            return "…";
        }

        // Never split a surrogate pair (emoji in an app name).
        int cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>"Discord (via SteelSeries Sonar)" style names, one line per kind, microphones first.</summary>
    private static IEnumerable<string> UsageLines(IReadOnlyList<UsageSession> active, string micPrefix, string cameraPrefix)
    {
        ArgumentNullException.ThrowIfNull(active);
        foreach (CaptureKind kind in new[] { CaptureKind.Microphone, CaptureKind.Camera })
        {
            List<string> names = [.. active
                .Where(s => s.Kind == kind && s.End is null)
                .Select(DescribeApp)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)];
            if (names.Count > 0)
            {
                yield return (kind == CaptureKind.Microphone ? micPrefix : cameraPrefix) + string.Join(", ", names);
            }
        }
    }

    private static string DescribeApp(UsageSession session) =>
        string.IsNullOrWhiteSpace(session.ViaApp)
            ? session.App.DisplayName
            : $"{session.App.DisplayName} (via {session.ViaApp})";

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
