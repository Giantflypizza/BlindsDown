namespace BlindsDown.Helper;

/// <summary>
/// Where the optional helper lives. The service runs as LocalSystem, so its program file must be somewhere only
/// administrators can change (Program Files), never in the user's Downloads or AppData: anyone who could replace
/// it would get SYSTEM rights. Its data folder in ProgramData is locked down to SYSTEM and Administrators.
/// </summary>
internal static class HelperPaths
{
    private const string LocalPipePrefix = @"\\.\pipe\";
    private const int MaxPipeNameLength = 200;

    /// <summary>%ProgramFiles%\BlindsDown</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlindsDown");

    /// <summary>%ProgramFiles%\BlindsDown\BlindsDown.exe</summary>
    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    /// <summary>%ProgramData%\BlindsDown</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlindsDown");

    public static string JournalFile => Path.Combine(DataDirectory, "device-lock-journal.json");

    /// <summary>The helper's log of lock changes, each with its request ID's fingerprint (see
    /// <see cref="BlindsDown.Core.Helper.FileHelperChangeStore"/>). Next to the journal in the same protected folder:
    /// only SYSTEM and administrators can change it, so no other program can rewrite whose change it was. Kept on
    /// disk so a restart of the helper neither hides a change from the tray app nor forgets the IDs that made them (a
    /// reused one is refused).</summary>
    public static string ChangeLogFile => Path.Combine(DataDirectory, "lock-changes.json");

    public static string LogFile => Path.Combine(DataDirectory, "helper.log");

    /// <summary>Log of the elevated install, update and removal runs. They may run as a different administrator
    /// account than the signed-in user, so never under the user's profile.</summary>
    public static string SetupLogFile => Path.Combine(DataDirectory, "setup.log");

    /// <summary>The installed program's file name, whatever the downloaded copy was called.</summary>
    internal const string ExeName = "BlindsDown.exe";

    /// <summary>
    /// "\\.\pipe\" + <paramref name="pipeName"/>: a pipe on THIS computer only ("\\server\pipe\..." would be the
    /// network). The name is restricted to letters, digits, '.', '-' and '_' because "\\.\" paths are normalised:
    /// a name like "..\x" would leave the pipe namespace.
    /// </summary>
    public static string LocalPipePath(string pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            throw new ArgumentException($"\"{pipeName}\" is not a valid pipe name for BlindsDown.", nameof(pipeName));
        }

        return LocalPipePrefix + pipeName;
    }

    internal static bool IsValidPipeName(string? pipeName) =>
        !string.IsNullOrEmpty(pipeName)
        && pipeName.Length <= MaxPipeNameLength
        && pipeName.Trim('.').Length > 0
        && pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
