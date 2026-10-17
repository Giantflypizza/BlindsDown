using System.Globalization;
using BlindsDown.Core.Usage;

namespace BlindsDown.Ui.Forms;

/// <summary>One day of history, newest record first.</summary>
internal sealed record HistoryDay(DateOnly Day, string Label, IReadOnlyList<UsageRecord> Records);

/// <summary>
/// Formatting for the Activity window. History is stored in UTC and always shown in local time; the
/// time zone and culture are parameters so the rules are tested without depending on the machine.
/// </summary>
internal static class ActivityText
{
    /// <summary>Apps often close and re-open the mic for a moment; gaps up to this long are shown as one use.</summary>
    public static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(15);

    public const string ApproximateMark = "≈";

    public static DateTimeOffset ToLocal(DateTimeOffset time, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(time, zone);

    public static DateOnly LocalDay(DateTimeOffset time, TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(time, zone).DateTime);

    /// <summary>"Today", "Yesterday", then the weekday and date (with the year once it is not this year).</summary>
    public static string DayLabel(DateOnly day, DateOnly today, CultureInfo culture)
    {
        if (day == today)
        {
            return "Today";
        }

        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }

        string pattern = day.Year == today.Year
            ? "dddd, " + culture.DateTimeFormat.MonthDayPattern
            : culture.DateTimeFormat.LongDatePattern;
        return day.ToDateTime(TimeOnly.MinValue).ToString(pattern, culture);
    }

    public static string Time(DateTimeOffset time, TimeZoneInfo zone, CultureInfo culture) =>
        ToLocal(time, zone).ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    /// <summary>"&lt; 1 min", "45 min", "2 h", "1 h 5 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        int minutes = (int)Math.Round(Math.Max(0, duration.TotalMinutes), MidpointRounding.AwayFromZero);
        if (minutes == 0)
        {
            return "< 1 min";
        }

        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        int hours = minutes / 60;
        int rest = minutes % 60;
        string h = hours.ToString(CultureInfo.InvariantCulture) + " h";
        return rest == 0 ? h : h + " " + rest.ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>A running timer for "Using now": "0:42", "12:05", "1:02:03".</summary>
    public static string Live(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        int hours = (int)duration.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}:{duration.Seconds:00}");
    }

    /// <summary>"14:02 – 14:47 (45 min)", prefixed with "≈" when the times were backfilled from Windows' records.</summary>
    public static string Range(UsageRecord record, TimeZoneInfo zone, CultureInfo culture) =>
        record.Approximate ? ApproximateMark + " " + PlainRange(record, zone, culture) : PlainRange(record, zone, culture);

    /// <summary>Start time of a live session: just the time today, otherwise with the day.</summary>
    public static string Since(DateTimeOffset start, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        DateOnly startDay = LocalDay(start, zone);
        DateOnly today = LocalDay(now, zone);
        string time = Time(start, zone, culture);
        if (startDay >= today)
        {
            return time;
        }

        return startDay == today.AddDays(-1)
            ? "Yesterday " + time
            : ToLocal(start, zone).ToString(culture.DateTimeFormat.MonthDayPattern, culture) + " " + time;
    }

    /// <summary>The app, with the relaying app for virtual devices: "Discord (via SteelSeries Sonar)".</summary>
    public static string App(string appName, string? viaApp) =>
        string.IsNullOrWhiteSpace(viaApp) ? appName : $"{appName} (via {viaApp})";

    /// <summary>The device name, or a generic label when the source could not tell which device (usage registry).</summary>
    public static string Device(CaptureKind kind, string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName)
            ? (kind == CaptureKind.Camera ? "A camera" : "A microphone")
            : deviceName;

    /// <summary>A full sentence for screen readers and tooltips.</summary>
    public static string Sentence(UsageRecord record, TimeZoneInfo zone, CultureInfo culture)
    {
        string what = record.Kind == CaptureKind.Camera ? "your camera" : "your microphone";
        string via = string.IsNullOrWhiteSpace(record.ViaApp) ? string.Empty : " via " + record.ViaApp;
        string approx = record.Approximate ? " (approximate times)" : string.Empty;
        return $"{record.AppName} used {what} ({Device(record.Kind, record.DeviceName)}) {PlainRange(record, zone, culture)}{via}{approx}.";
    }

    /// <summary>Merges short gaps and groups by local day, newest day and newest record first.</summary>
    public static List<HistoryDay> GroupByDay(IEnumerable<UsageRecord> history, TimeZoneInfo zone, DateTimeOffset now, CultureInfo culture)
    {
        DateOnly today = LocalDay(now, zone);
        return HistoryStore.MergeShortGaps(history, MergeGap)
            .GroupBy(r => LocalDay(r.Start, zone))
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryDay(g.Key, DayLabel(g.Key, today, culture), g.OrderByDescending(r => r.Start).ToList()))
            .ToList();
    }

    /// <summary>
    /// Rebuilds the app identity from a history record so "Always allow" works from history rows too.
    /// The record stores <see cref="AppIdentity.GroupKey"/>; the identity is built so its GroupKey equals
    /// that stored key again (allow rules are matched on GroupKey).
    /// </summary>
    public static AppIdentity IdentityOf(UsageRecord record)
    {
        const string PackagePrefix = "pkg:";
        const string ExePrefix = "exe:";
        if (record.AppKey.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new AppIdentity(record.AppKey, record.AppName, record.ExePath, record.AppKey[PackagePrefix.Length..]);
        }

        if (!string.IsNullOrEmpty(Path.GetFileName(record.ExePath)))
        {
            return new AppIdentity(record.AppKey, record.AppName, record.ExePath, null);
        }

        // No exe path: GroupKey was "exe:" + KEY + "|" + NAME, so recover KEY.
        string nameSuffix = "|" + record.AppName.ToUpperInvariant();
        if (record.AppKey.StartsWith(ExePrefix, StringComparison.OrdinalIgnoreCase)
            && record.AppKey.EndsWith(nameSuffix, StringComparison.Ordinal)
            && record.AppKey.Length > ExePrefix.Length + nameSuffix.Length)
        {
            string key = record.AppKey[ExePrefix.Length..^nameSuffix.Length];
            return new AppIdentity(key, record.AppName, null, null);
        }

        return new AppIdentity(record.AppKey, record.AppName, record.ExePath, null);
    }

    private static string PlainRange(UsageRecord record, TimeZoneInfo zone, CultureInfo culture) =>
        $"{Time(record.Start, zone, culture)} – {Time(record.End, zone, culture)} ({Duration(record.End - record.Start)})";
}
