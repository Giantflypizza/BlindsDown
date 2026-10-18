using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>One file to install: where it comes from, and its path inside the install folder.</summary>
internal sealed record CopyItem(string Source, string RelativeTarget);

/// <summary>Files the setup put in place that can still be taken back.</summary>
internal interface IInstalledFiles
{
    /// <summary>Setup succeeded: delete the replaced old files (now or, when in use, at the next restart).</summary>
    void Commit();

    /// <summary>Setup failed: remove the new files and put the old ones back.</summary>
    void Undo();
}

/// <summary>
/// The helper's program files in Program Files. The release build is ONE self-contained exe, so that is all the
/// helper needs. A development build ("dotnet build") is framework-dependent: BlindsDown.exe only starts
/// BlindsDown.dll and its neighbours, so the whole app folder is copied (its files and its runtimes\ folder, only
/// program file types).
/// </summary>
internal static partial class HelperFiles
{
    /// <summary>Written into the install folder when setup creates it: only then does removing the helper remove the
    /// whole folder (see <see cref="RemoveInstallFolder"/>).</summary>
    internal const string FolderMarkerName = ".blindsdown-helper";

    /// <summary>Older SDKs wrote this next to a development build: it tells .NET to also load code from the user's
    /// own NuGet folder, which a program running as SYSTEM must never do.</summary>
    private const string DevRuntimeConfigSuffix = ".runtimeconfig.dev.json";

    /// <summary>NT SERVICE\TrustedInstaller: the same on every Windows PC (a service SID comes from the service's name).</summary>
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Reading, listing and running only (FILE_GENERIC_READ | FILE_GENERIC_EXECUTE).</summary>
    private const uint ReadOnlyRights = 0x001200A9;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericAll = 0x10000000;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;
    private const uint FileGenericExecute = 0x001200A0;
    private const uint FileAllAccess = 0x001F01FF;

    /// <summary>What an app folder holds that the program needs: code, its manifests (.deps.json, .runtimeconfig.json)
    /// and debug symbols (for readable errors in the helper's log). Never logs or anything else lying around.</summary>
    private static readonly FrozenSet<string> ProgramFileExtensions =
        new[] { ".exe", ".dll", ".json", ".pdb" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plan for the running program. Throws <see cref="InvalidOperationException"/> when it isn't
    /// BlindsDown's own exe on a local disk (see <see cref="SafeShell.OwnExecutable"/>).</summary>
    public static IReadOnlyList<CopyItem> CurrentPlan()
    {
        string exe = SafeShell.OwnExecutable()
            ?? throw new InvalidOperationException("The camera helper can only be installed from BlindsDown.exe on a local disk.");
        string baseDirectory = AppContext.BaseDirectory;
        if (IsSingleFile())
        {
            return BuildPlan(exe, baseDirectory, singleFile: true, [], []);
        }

        string runtimes = Path.Combine(baseDirectory, "runtimes");
        return BuildPlan(
            exe,
            baseDirectory,
            singleFile: false,
            Directory.EnumerateFiles(baseDirectory),
            Directory.Exists(runtimes) ? Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories) : []);
    }

    /// <summary>
    /// The running exe always becomes <see cref="HelperPaths.ExeName"/>, whatever the download was called. In a
    /// development build, the other program files of the app folder follow at the same relative paths.
    /// </summary>
    internal static IReadOnlyList<CopyItem> BuildPlan(
        string processPath,
        string baseDirectory,
        bool singleFile,
        IEnumerable<string> topLevelFiles,
        IEnumerable<string> runtimesFiles)
    {
        var plan = new List<CopyItem> { new(processPath, HelperPaths.ExeName) };
        if (singleFile)
        {
            return plan;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HelperPaths.ExeName };
        foreach (string file in topLevelFiles.Concat(runtimesFiles))
        {
            string relative = Path.GetRelativePath(baseDirectory, file);
            if (SamePath(file, processPath)
                || !IsInsideFolder(relative)
                || !ProgramFileExtensions.Contains(Path.GetExtension(file))
                || file.EndsWith(DevRuntimeConfigSuffix, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(relative))
            {
                continue;
            }

            plan.Add(new CopyItem(file, relative));
        }

        return plan;
    }

    /// <summary>Total size of the files in <paramref name="directory"/>, for "Installed apps". Links are not followed,
    /// and what can't be read counts as nothing: the size is only shown, never worth failing a setup step for.</summary>
    public static long TotalBytes(string directory)
    {
        long total = 0;
        try
        {
            if (!IsRealDirectory(directory))
            {
                return 0;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    total += (attributes & FileAttributes.Directory) != 0 ? TotalBytes(entry) : new FileInfo(entry).Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not add up the size of {directory}: {ex.Message}");
        }

        return total;
    }

    /// <summary>
    /// Removes the helper's program folder. The whole folder goes only when helper setup created it (it holds
    /// <see cref="FolderMarkerName"/>). A folder that was there before, such as BlindsDown unzipped into Program Files
    /// by its user or put there by an installer, may hold that user's own copy of BlindsDown: then only setup's own
    /// leftovers go (replaced files kept for an undo, unfinished staging folders) and everything else stays.
    /// Returns false when something could neither be deleted nor scheduled.
    /// </summary>
    public static bool RemoveInstallFolder(string directory)
    {
        if (!IsRealDirectory(directory) || File.Exists(Path.Combine(directory, FolderMarkerName)))
        {
            // Setup's own folder; or nothing there, or a link (only the link goes).
            return RemoveTree(directory);
        }

        Log.Info($"{directory} was there before the helper was installed, so it stays; only setup's own leftovers in it are deleted.");
        return RemoveSetupLeftovers(directory);
    }

    /// <summary>
    /// Deletes a folder BlindsDown created (under Program Files or ProgramData, where only administrators can change
    /// things). Files in use, such as a running BlindsDown.exe, are deleted at the next restart. Returns false when
    /// something could neither be deleted nor scheduled.
    /// Links (junctions, symbolic links) are deleted themselves, never followed: .NET's recursive listing walks into
    /// a junction, and this runs as an administrator, so following one would delete whatever it points to.
    /// </summary>
    public static bool RemoveTree(string path)
    {
        FileAttributes attributes;
        List<string> entries = [];
        try
        {
            attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                entries.AddRange(Directory.EnumerateFileSystemEntries(path));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
            return false;
        }

        bool complete = true;
        foreach (string entry in entries)
        {
            complete &= RemoveTree(entry);
        }

        // The contents go first, or are scheduled first: Windows removes a folder at restart only once it is empty.
        return RemoveEntry(path, attributes) && complete;
    }

    /// <summary>
    /// True when only accounts <paramref name="mayChange"/> accepts can change what <paramref name="security"/> protects,
    /// or anything made inside it later: owned by one of them (an owner can always rewrite the access list), and
    /// nobody else allowed more than reading and running, whether an entry is explicit or inherited, and whether it
    /// applies here or only to what is made inside. Deny entries only take rights away. "CREATOR OWNER" entries that
    /// only pass to new files and folders give their makers rights, and only the accepted accounts can make anything
    /// here. Unlike <see cref="HelperDataDirectory.IsSecure"/>, entries inherited from Program Files are fine: they are
    /// what makes an ordinary Program Files folder safe.
    /// </summary>
    internal static bool OnlyTheseCanChange(FileSystemSecurity security, Func<SecurityIdentifier, bool> mayChange)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !mayChange(owner))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny || rule.IdentityReference is not SecurityIdentifier account)
            {
                continue;
            }

            bool harmless = mayChange(account)
                || ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0
                    && (account.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || account.IsWellKnown(WellKnownSidType.CreatorGroupSid)))
                || (FileRights(rule) & ~ReadOnlyRights) == 0;
            if (!harmless)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The accounts that may change the helper's program files: SYSTEM, Administrators, and TrustedInstaller
    /// (which owns Program Files itself).</summary>
    internal static bool IsAdministrativeAccount(SecurityIdentifier account) =>
        account.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || account.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || account.Value == TrustedInstallerSid;

    internal static bool IsInsideFolder(string relativePath) =>
        relativePath.Length > 0
        && !Path.IsPathRooted(relativePath)
        && !relativePath.Split('\\', '/').Any(part => part is "" or "." or "..");

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder that exists and is not a link.</summary>
    internal static bool IsRealDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The names <see cref="StagedInstall"/> gives its staging folders and the files it moves aside, with
    /// their 8-hex-digit tag: nothing else in a folder is ever taken for setup's own.</summary>
    internal static bool IsSetupLeftover(string name, bool isDirectory) =>
        isDirectory ? StagingFolderName().IsMatch(name) : MovedAsideFileName().IsMatch(name);

    private static bool RemoveSetupLeftovers(string directory)
    {
        bool complete = true;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).ToList())
            {
                FileAttributes attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (IsSetupLeftover(Path.GetFileName(entry), isDirectory))
                {
                    complete &= RemoveTree(entry);
                }
                else if (isDirectory && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    complete &= RemoveSetupLeftovers(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not look through {directory}: {ex.Message}");
            return false;
        }

        return complete;
    }

    /// <summary>The rule's rights as they apply to files. Program Files' inheritable entries use generic rights
    /// ("GR GE" for Users), which Windows turns into file rights only on the files and folders they pass to.</summary>
    private static uint FileRights(FileSystemAccessRule rule)
    {
        uint mask = unchecked((uint)rule.FileSystemRights);
        uint mapped = mask & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        mapped |= (mask & GenericRead) != 0 ? FileGenericRead : 0;
        mapped |= (mask & GenericWrite) != 0 ? FileGenericWrite : 0;
        mapped |= (mask & GenericExecute) != 0 ? FileGenericExecute : 0;
        mapped |= (mask & GenericAll) != 0 ? FileAllAccess : 0;
        return mapped;
    }

    [GeneratedRegex(@"^\.setup-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex StagingFolderName();

    [GeneratedRegex(@"\.(old|undo)-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex MovedAsideFileName();

    /// <summary>Deletes one file, empty folder or link (a link is removed itself, whatever it points to).</summary>
    private static bool RemoveEntry(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0)
        {
            return DeleteFileOrScheduleIt(path);
        }

        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DeleteAtRestart(path);
        }
    }

    internal static bool DeleteFileOrScheduleIt(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DeleteAtRestart(file);
        }
    }

    /// <summary>MOVEFILE_DELAY_UNTIL_REBOOT: for a file Windows won't let go of now (a running exe).</summary>
    internal static unsafe bool DeleteAtRestart(string path)
    {
        fixed (char* p = path)
        {
            if (HelperNative.MoveFileExW(p, null, HelperNative.MoveFileDelayUntilReboot) != 0)
            {
                Log.Info($"In use; it will be deleted at the next restart: {path}");
                return true;
            }
        }

        Log.Warn($"Could not delete {path} (Windows error {Marshal.GetLastPInvokeError()}).");
        return false;
    }

#pragma warning disable IL3000 // Assembly.Location is empty exactly when the app runs as a single file; that is the question here.
    private static bool IsSingleFile() => string.IsNullOrEmpty(typeof(HelperFiles).Assembly.Location);
#pragma warning restore IL3000
}

/// <summary>Who may change the install folder, and the security setup gives a folder it makes.</summary>
/// <param name="MayChange">The accounts that may change the folder and the files in it.</param>
/// <param name="NewFolderSddl">The security of a folder setup makes.</param>
internal sealed record InstallFolderSecurity(Func<SecurityIdentifier, bool> MayChange, string NewFolderSddl)
{
    /// <summary>The real install folder: only SYSTEM, Administrators and TrustedInstaller may change it. A folder setup
    /// makes is owned by Administrators, gives SYSTEM and Administrators full control and Users read and run (for the
    /// Installed apps icon and the version shown in Settings), and inherits nothing, so it doesn't depend on how
    /// Program Files is set up.</summary>
    public static InstallFolderSecurity AdministratorsOnly { get; } = new(
        HelperFiles.IsAdministrativeAccount,
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");

    /// <summary>Tests only (they run unelevated, in a temp folder): <paramref name="user"/> may change it too, and owns
    /// a folder setup makes. Such a folder inherits nothing, so other accounts' rights on the temp folder don't count.</summary>
    internal static InstallFolderSecurity ForTests(SecurityIdentifier user) => new(
        account => account == user || HelperFiles.IsAdministrativeAccount(account),
        $"O:{user.Value}D:P(A;OICI;FA;;;{user.Value})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
}

/// <summary>
/// Puts a copy plan into the install folder without ever leaving a half-copied program there: every file is first
/// copied completely into a staging folder inside the install folder (same disk, same administrator-only
/// permissions), then each is moved into place with a rename. Files it replaces are renamed aside (which Windows
/// allows even for a running exe) and kept until <see cref="Commit"/>, so <see cref="Undo"/> can put them back.
/// A folder it creates gets <see cref="HelperFiles.FolderMarkerName"/>; it never makes or follows a link.
/// </summary>
/// <remarks>
/// The service runs these files as SYSTEM, so anyone who can change them, or the folders they are in, could get
/// SYSTEM's rights. A folder setup makes gets administrator-only security. A folder that was already there, such as
/// BlindsDown unzipped into Program Files, or moved there from the user's own folder (a move keeps the folder's
/// permissions, which then still let its user change it), is used only when only administrators can change it,
/// along with every subfolder on the way to a file and every file that is used as it is. Otherwise setup stops and
/// says why; it never "repairs" the permissions of a folder someone else made.
/// </remarks>
internal sealed class StagedInstall : IInstalledFiles
{
    private readonly string _installDirectory;
    private readonly bool _createdDirectory;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly List<(string Target, string? Backup)> _placed = [];

    private StagedInstall(string installDirectory, bool createdDirectory)
    {
        _installDirectory = installDirectory;
        _createdDirectory = createdDirectory;
    }

    /// <summary>Copies <paramref name="plan"/> into <paramref name="installDirectory"/>. On failure everything is
    /// undone before the exception is rethrown. Files that already are the install folder's own are skipped
    /// (BlindsDown running from the install folder itself). Throws <see cref="IOException"/> when the folder, or
    /// something in it that would be used, can be changed by others than administrators.</summary>
    public static StagedInstall Apply(IReadOnlyList<CopyItem> plan, string installDirectory) =>
        Apply(plan, installDirectory, InstallFolderSecurity.AdministratorsOnly);

    /// <summary>As <see cref="Apply(IReadOnlyList{CopyItem}, string)"/>, with the given security (tests).</summary>
    internal static StagedInstall Apply(IReadOnlyList<CopyItem> plan, string installDirectory, InstallFolderSecurity security)
    {
        bool created = !Directory.Exists(installDirectory);
        if (!created && !HelperFiles.IsRealDirectory(installDirectory))
        {
            // A junction or link would put the program that runs as SYSTEM wherever it points, maybe somewhere
            // normal users can change it.
            throw new IOException($"{installDirectory} is a link, not a folder; the helper is not installed through it.");
        }

        if (created)
        {
            // Made with its security in one step, so it is never briefly open to others.
            var folderSecurity = new DirectorySecurity();
            folderSecurity.SetSecurityDescriptorSddlForm(security.NewFolderSddl);
            new DirectoryInfo(installDirectory).Create(folderSecurity);
        }

        var install = new StagedInstall(installDirectory, created);
        string staging = Path.Combine(installDirectory, ".setup-" + install._tag);
        try
        {
            // Also for a folder made just now: if another one appeared in between, making it did nothing.
            RequireOnlyAdministrators(installDirectory, isFolder: true, security);
            if (created)
            {
                File.WriteAllText(
                    Path.Combine(installDirectory, HelperFiles.FolderMarkerName),
                    "BlindsDown's camera helper setup made this folder; removing the helper removes it.\r\n");
            }

            var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<CopyItem>();
            foreach (CopyItem item in plan)
            {
                string target = Path.Combine(installDirectory, item.RelativeTarget);
                RequireSafeFoldersOnTheWay(installDirectory, target, security, checkedFolders);
                if (HelperFiles.SamePath(item.Source, target))
                {
                    // Used as it is (BlindsDown runs from the install folder): its own permissions count too.
                    RequireOnlyAdministrators(target, isFolder: false, security);
                    continue;
                }

                string staged = Path.Combine(staging, item.RelativeTarget);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(item.Source, staged, overwrite: false);
                pending.Add(item);
            }

            foreach (CopyItem item in pending)
            {
                string target = Path.Combine(installDirectory, item.RelativeTarget);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = File.Exists(target) ? $"{target}.old-{install._tag}" : null;
                if (backup is not null)
                {
                    File.Move(target, backup);
                }

                install._placed.Add((target, backup));
                File.Move(Path.Combine(staging, item.RelativeTarget), target);
            }
        }
        catch
        {
            install.Undo();
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                _ = HelperFiles.RemoveTree(staging);
            }
        }

        Log.Info($"Copied {install._placed.Count} file(s) to {installDirectory}.");
        return install;
    }

    /// <summary>Every folder that already exists between the install folder and <paramref name="target"/> must be a
    /// real folder (not a link) that only administrators can change.</summary>
    private static void RequireSafeFoldersOnTheWay(string installDirectory, string target, InstallFolderSecurity security, HashSet<string> checkedFolders)
    {
        string? folder = Path.GetDirectoryName(target);
        var onTheWay = new List<string>();
        while (folder is not null && !HelperFiles.SamePath(folder, installDirectory))
        {
            onTheWay.Add(folder);
            folder = Path.GetDirectoryName(folder);
        }

        // From the install folder down: a link higher up must be found before anything below it is looked at.
        for (int i = onTheWay.Count - 1; i >= 0; i--)
        {
            string path = onTheWay[i];
            if (!checkedFolders.Add(path) || !Path.Exists(path))
            {
                continue;
            }

            if (!HelperFiles.IsRealDirectory(path))
            {
                throw new IOException($"{path} is not a folder (or is a link); the helper is not installed through it.");
            }

            RequireOnlyAdministrators(path, isFolder: true, security);
        }
    }

    private static void RequireOnlyAdministrators(string path, bool isFolder, InstallFolderSecurity security)
    {
        bool trusted;
        try
        {
            const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity current = isFolder
                ? new DirectoryInfo(path).GetAccessControl(Sections)
                : new FileInfo(path).GetAccessControl(Sections);
            trusted = HelperFiles.OnlyTheseCanChange(current, security.MayChange);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // Not even administrators may read its permissions: certainly not a folder to trust.
            trusted = false;
        }

        if (!trusted)
        {
            throw new IOException(
                $"{path} can be changed by accounts other than administrators, so the camera helper, which runs as SYSTEM, "
                + "is not installed there. Remove it (or give it the usual Program Files permissions), then try again.");
        }
    }

    public void Commit()
    {
        foreach ((_, string? backup) in _placed)
        {
            if (backup is not null && File.Exists(backup))
            {
                _ = HelperFiles.DeleteFileOrScheduleIt(backup);
            }
        }
    }

    public void Undo()
    {
        for (int i = _placed.Count - 1; i >= 0; i--)
        {
            (string target, string? backup) = _placed[i];
            try
            {
                if (File.Exists(target))
                {
                    try
                    {
                        File.Delete(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Still in use: rename it out of the way so the old file can go back, delete it later.
                        string aside = $"{target}.undo-{_tag}";
                        File.Move(target, aside);
                        _ = HelperFiles.DeleteAtRestart(aside);
                    }
                }

                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not put back {target}", ex);
            }
        }

        _placed.Clear();
        if (_createdDirectory)
        {
            _ = HelperFiles.RemoveTree(_installDirectory);
        }
    }
}
