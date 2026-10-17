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

    public string LogFile => Path.Combine(DataDirectory, "blindsdown.log");
}
