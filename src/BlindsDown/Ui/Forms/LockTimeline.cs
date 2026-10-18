using System.Globalization;
using BlindsDown.Core.Usage;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// One lock with its unlock, for the Activity timeline. Either end can be missing: <see cref="Ongoing"/> is the
/// lock that is on right now; an older lock without an unlock is one whose unlock was never recorded (BlindsDown
/// was closed or crashed while locked); an unlock without a lock belongs to a lock older than the kept history.
/// </summary>
internal sealed record LockPeriod(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    LockEventSource? LockedBy,
    LockEventSource? UnlockedBy,
    string? LockSummary,
    string? UnlockSummary,
    bool HadProblems,
    bool Ongoing)
{
    /// <summary>Where the period sits in the timeline: its start, or the unlock when the start isn't known.</summary>
    public DateTimeOffset At => Start ?? End ?? DateTimeOffset.MinValue;
}

/// <summary>
/// Turns the lock event log (<see cref="LockEventRecord"/>) into periods and words for the Activity window:
/// "⚠ Locked · hotkey / tray" over "14:02 – 14:47 (45 min)", with the full story as the tooltip. Pure functions;
/// the time zone and culture are parameters, as in <see cref="ActivityText"/>.
/// </summary>
internal static class LockTimeline
{
    /// <summary>Marks a lock that was only partial or an unlock that couldn't restore everything exactly.</summary>
    public const string ProblemMark = "⚠";

    /// <summary>
    /// Pairs each lock with the unlock that follows it, newest period first. Events are sorted by time first
    /// (the log may come in any order, and equal times keep their order). Two locks in a row mean the first one's
    /// unlock was never recorded; an unlock with no lock before it gets a period without a start. An update changes
    /// the open lock's summary (a problem it reports stays marked); without an open lock it is left out.
    /// </summary>
    public static List<LockPeriod> Pair(IEnumerable<LockEventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var periods = new List<LockPeriod>();
        LockEventRecord? open = null;
        foreach (LockEventRecord record in events.OrderBy(e => e.At))
        {
            if (record.Action == LockEventAction.Update)
            {
                if (open is not null)
                {
                    open = open with { Summary = record.Summary ?? open.Summary, HadProblems = open.HadProblems || record.HadProblems };
                }

                continue;
            }

            if (record.Action == LockEventAction.Lock)
            {
                if (open is not null)
                {
                    periods.Add(Period(open, unlock: null, ongoing: false));
                }

                open = record;
            }
            else
            {
                periods.Add(open is null
                    ? new LockPeriod(null, record.At, null, record.Source, null, record.Summary, record.HadProblems, Ongoing: false)
                    : Period(open, record, ongoing: false));
                open = null;
            }
        }

        if (open is not null)
        {
            periods.Add(Period(open, unlock: null, ongoing: true));
        }

        periods.Reverse();
        return periods;
    }

    /// <summary>
    /// The first column: "Locked · hotkey / tray" (how it was locked / how it was unlocked; one word when both
    /// were the same), "⚠" first when something went wrong.
    /// </summary>
    public static string Title(LockPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        string how = (period.LockedBy, period.UnlockedBy) switch
        {
            (LockEventSource locked, LockEventSource unlocked) when locked != unlocked => Source(locked) + " / " + Source(unlocked),
            (LockEventSource locked, _) => Source(locked),
            (null, LockEventSource unlocked) => Source(unlocked),
            _ => "unknown",
        };
        string text = (period.LockedBy is null ? "Unlocked · " : "Locked · ") + how;
        return period.HadProblems ? ProblemMark + " " + text : text;
    }

    /// <summary>The "When" column: "14:02 – 14:47 (45 min)", "14:02 – now", "14:02, unlock not recorded", or just the unlock time.</summary>
    public static string Range(LockPeriod period, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(period);
        if (period.Start is not DateTimeOffset start)
        {
            return period.End is DateTimeOffset end ? ActivityText.Time(end, zone, culture) : string.Empty;
        }

        string from = ActivityText.Time(start, zone, culture);
        if (period.End is DateTimeOffset until)
        {
            return $"{from} – {ActivityText.Time(until, zone, culture)} ({ActivityText.Duration(until - start)})";
        }

        return period.Ongoing ? from + " – now" : from + ", unlock not recorded";
    }

    /// <summary>The "Device" column: what the lock covered ("4 microphones, 1 camera"), when known.</summary>
    public static string Devices(LockPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period.LockSummary ?? string.Empty;
    }

    /// <summary>The whole story for the tooltip and screen readers, with both summaries.</summary>
    public static string Sentence(LockPeriod period, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(period);
        var parts = new List<string>();
        if (period.Start is DateTimeOffset start)
        {
            string locked = "Locked " + Phrase(period.LockedBy) + " at " + ActivityText.Time(start, zone, culture);
            if (period.End is DateTimeOffset end)
            {
                locked += ", unlocked " + Phrase(period.UnlockedBy) + " at " + ActivityText.Time(end, zone, culture)
                    + " (" + ActivityText.Duration(end - start) + ").";
            }
            else
            {
                locked += period.Ongoing ? ". Still locked." : ". No unlock was recorded after it.";
            }

            parts.Add(locked);
        }
        else if (period.End is DateTimeOffset end)
        {
            parts.Add("Unlocked " + Phrase(period.UnlockedBy) + " at " + ActivityText.Time(end, zone, culture) + ".");
        }

        if (!string.IsNullOrWhiteSpace(period.LockSummary))
        {
            parts.Add("Locked: " + EndSentence(period.LockSummary));
        }

        if (!string.IsNullOrWhiteSpace(period.UnlockSummary))
        {
            parts.Add("Unlocked: " + EndSentence(period.UnlockSummary));
        }

        if (period.HadProblems)
        {
            parts.Add("Not everything went as planned.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>Short source words for the title column.</summary>
    public static string Source(LockEventSource source) => source switch
    {
        LockEventSource.Hotkey => "hotkey",
        LockEventSource.Tray => "tray",
        LockEventSource.OtherProgram => "other program",
        LockEventSource.Automatic => "automatic",
        LockEventSource.Recovery => "startup",
        LockEventSource.Exit => "exit",
        _ => source.ToString().ToLowerInvariant(),
    };

    private static LockPeriod Period(LockEventRecord lockEvent, LockEventRecord? unlock, bool ongoing) =>
        new(
            lockEvent.At,
            unlock?.At,
            lockEvent.Source,
            unlock?.Source,
            lockEvent.Summary,
            unlock?.Summary,
            lockEvent.HadProblems || unlock?.HadProblems == true,
            ongoing);

    /// <summary>"with the hotkey", "from the tray icon", "by another program"... for the sentence.</summary>
    private static string Phrase(LockEventSource? source) => source switch
    {
        LockEventSource.Hotkey => "with the hotkey",
        LockEventSource.Tray => "from the tray icon",
        LockEventSource.OtherProgram => "by another program",
        LockEventSource.Automatic => "automatically",
        LockEventSource.Recovery => "at startup",
        LockEventSource.Exit => "when BlindsDown closed",
        _ => "in another way",
    };

    private static string EndSentence(string text)
    {
        string trimmed = text.Trim();
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }
}
