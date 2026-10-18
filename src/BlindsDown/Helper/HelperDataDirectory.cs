using System.Security.AccessControl;
using System.Security.Principal;

namespace BlindsDown.Helper;

/// <summary>What <see cref="HelperDataDirectory.Prepare"/> did.</summary>
/// <param name="Ready">The folder exists and only SYSTEM and Administrators can change it.</param>
/// <param name="MovedAside">Where an untrusted folder (or file) found at the path was moved, if one was.</param>
/// <param name="Problem">Why the folder isn't ready.</param>
internal sealed record DataDirectoryResult(bool Ready, string? MovedAside, string? Problem);

/// <summary>
/// The helper's data folder (%ProgramData%\BlindsDown): the device lock journal and the logs. Only SYSTEM and
/// Administrators may change it, because the journal tells a SYSTEM service which devices to turn off and on.
/// ProgramData lets every user create folders, so a folder found there may have been made by anyone, may be a
/// link to somewhere else, and may hold links to system files. It is trusted only when its own security says that
/// only SYSTEM and Administrators can change it. Anything else is moved aside with ONE rename (a link is renamed,
/// never followed) and the folder is created anew. It is never repaired in place or deleted file by file: as an
/// administrator, changing or deleting things inside a folder someone else controls can be redirected (through
/// links swapped in at the right moment) to change or delete system files.
/// </summary>
internal static class HelperDataDirectory
{
    /// <summary>Owner Administrators; a protected DACL (nothing inherited from ProgramData) giving SYSTEM and
    /// Administrators full control, inherited by every file inside.</summary>
    internal const string Sddl = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private const int Attempts = 4;

    /// <summary>Reading and listing only. Anything else (writing, deleting, changing permissions or the owner,
    /// generic rights) lets an account change the journal.</summary>
    private const FileSystemRights ReadOnlyRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    /// <summary>Makes sure <paramref name="path"/> exists and is trustworthy. Runs as SYSTEM (service start) or
    /// as an elevated administrator (setup), before logging starts, so it reports instead of logging.</summary>
    public static DataDirectoryResult Prepare(string path)
    {
        string? movedAside = null;
        string? problem = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (directory.Exists && IsTrusted(directory))
                {
                    return new DataDirectoryResult(true, movedAside, null);
                }

                if (directory.Exists || File.Exists(path))
                {
                    movedAside = MoveAside(path, directory.Exists);
                    continue;
                }

                // Created with its security in one step, so it is never briefly open to everyone. If someone
                // else creates it first, this does nothing and the next round checks (and moves aside) theirs.
                directory.Create(NewSecurity());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                problem = ex.Message;
            }
        }

        return new DataDirectoryResult(false, movedAside, problem ?? $"Could not prepare {path}.");
    }

    /// <summary>
    /// True when only SYSTEM and Administrators can change what <paramref name="security"/> protects: owned by one
    /// of them (an owner can always rewrite the access list), nothing inherited from the parent folder, and nobody
    /// else allowed more than reading. Read-only entries for others (an administrator letting everyone read the
    /// logs, say) and "deny" entries (which only take rights away) don't make it untrusted.
    /// </summary>
    internal static bool IsSecure(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrustedAccount(owner))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            bool harmless = rule.AccessControlType == AccessControlType.Deny
                || (rule.IdentityReference is SecurityIdentifier account && IsTrustedAccount(account))
                || (rule.FileSystemRights & ~ReadOnlyRights) == 0;
            if (!harmless)
            {
                return false;
            }
        }

        return true;
    }

    internal static DirectorySecurity NewSecurity()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(Sddl);
        return security;
    }

    private static bool IsTrustedAccount(SecurityIdentifier account) =>
        account.IsWellKnown(WellKnownSidType.LocalSystemSid) || account.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static bool IsTrusted(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // A junction or symbolic link: its target could be anything, so never use or follow it.
            return false;
        }

        try
        {
            return IsSecure(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // Not even administrators may read its security: certainly not ours.
            return false;
        }
    }

    private static string MoveAside(string path, bool isDirectory)
    {
        string aside = $"{path}.untrusted-{Guid.NewGuid().ToString("N")[..8]}";
        if (isDirectory)
        {
            Directory.Move(path, aside);
        }
        else
        {
            File.Move(path, aside);
        }

        return aside;
    }
}
