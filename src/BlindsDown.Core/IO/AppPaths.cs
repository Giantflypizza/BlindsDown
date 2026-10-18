namespace BlindsDown.Core.IO;

/// <summary>Where BlindsDown keeps its files. Everything is local to this PC and this Windows user.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>%LocalAppData%\BlindsDown (not roaming: device IDs are specific to this PC).</summary>
    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlindsDown"));

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string JournalFile => Path.Combine(DataDirectory, "lock-journal.json");

    public string PendingRestoreFile => Path.Combine(DataDirectory, "pending-restores.json");

    public string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    /// <summary>Lock and unlock events (when, how, from where), shown in the Activity timeline.</summary>
    public string LockEventsFile => Path.Combine(DataDirectory, "lock-events.jsonl");

    /// <summary>The newest camera helper change seen, and the IDs of this user's own helper requests, so a change by
    /// another program is noticed even when it happened while BlindsDown wasn't running.</summary>
    public string HelperStateFile => Path.Combine(DataDirectory, "helper-state.txt");

    public string LogFile => Path.Combine(DataDirectory, "blindsdown.log");
}
