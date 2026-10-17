namespace BlindsDown.Core.Usage;

public enum CaptureKind
{
    Microphone,
    Camera,
}

/// <summary>
/// Who is using a device. <see cref="Key"/> is stable across app updates where possible:
/// the package family name for Store apps, otherwise the lower-case exe path.
/// </summary>
public sealed record AppIdentity(string Key, string DisplayName, string? ExePath, string? PackageFamilyName)
{
    public static AppIdentity Unknown { get; } = new("unknown", "Unknown app", null, null);

    /// <summary>
    /// Key used for "always allow" rules and history grouping: the package family for Store apps,
    /// otherwise the full exe path with version folders replaced by "*" (Discord's "app-1.0.9262",
    /// WebView2's "118.0.2088.46"), so updates don't look like a new app. The folder is part of the key
    /// on purpose: a program elsewhere that merely copies an allowed app's file name and description
    /// must not inherit its "always allow".
    /// </summary>
    public string GroupKey => PackageFamilyName is not null
        ? "pkg:" + PackageFamilyName.ToUpperInvariant()
        : ExePath is { Length: > 0 } path
            ? "exe:" + WithoutVersionFolders(path).ToUpperInvariant()
            : "exe:" + Key.ToUpperInvariant() + "|" + DisplayName.ToUpperInvariant();

    private static readonly System.Text.RegularExpressions.Regex VersionFolder = new(
        @"^(app-)?v?\d+(\.\d+)+([-_+][0-9A-Za-z.]+)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"C:\...\Discord\app-1.0.9262\Discord.exe" becomes "C:\...\Discord\*\Discord.exe".</summary>
    public static string WithoutVersionFolders(string exePath)
    {
        string[] parts = exePath.Split('\\');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (VersionFolder.IsMatch(parts[i]))
            {
                parts[i] = "*";
            }
        }

        return string.Join('\\', parts);
    }
}

/// <summary>One source's report that an app is using a capture device right now.</summary>
public sealed record UsageSignal(
    CaptureKind Kind,
    AppIdentity App,
    int? ProcessId,
    string DeviceId,
    string DeviceName,
    string? ViaApp = null)
{
    /// <summary>True when the source can't tell which device is in use (e.g. the usage registry).</summary>
    public bool DeviceUnknown => string.IsNullOrEmpty(DeviceId);
}

/// <summary>A period during which one app used one device.</summary>
public sealed record UsageSession
{
    public required Guid Id { get; init; }

    public required CaptureKind Kind { get; init; }

    public required AppIdentity App { get; init; }

    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>For virtual devices: the app that relays the real device, e.g. "SteelSeries Sonar".</summary>
    public string? ViaApp { get; init; }

    public required DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public required IReadOnlyList<string> Sources { get; init; }

    public TimeSpan Duration(DateTimeOffset now) => (End ?? now) - Start;
}

/// <summary>A finished session as stored in history.jsonl (one JSON object per line).</summary>
public sealed record UsageRecord
{
    public required CaptureKind Kind { get; init; }

    public required string AppKey { get; init; }

    public required string AppName { get; init; }

    public string? ExePath { get; init; }

    public required string DeviceName { get; init; }

    public string? ViaApp { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    /// <summary>True for entries backfilled from Windows' "last used" registry data (times approximate).</summary>
    public bool Approximate { get; init; }

    public static UsageRecord From(UsageSession s, DateTimeOffset end) => new()
    {
        Kind = s.Kind,
        AppKey = s.App.GroupKey,
        AppName = s.App.DisplayName,
        ExePath = s.App.ExePath,
        DeviceName = s.DeviceName,
        ViaApp = s.ViaApp,
        Start = s.Start,
        End = end,
    };
}
