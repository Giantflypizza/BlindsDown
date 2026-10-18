using System.Security.AccessControl;
using System.Security.Principal;
using BlindsDown.Helper;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// When the helper trusts its data folder. The security checks run on in-memory descriptors; the folder handling
/// only ever runs inside a temp folder (ProgramData is never touched).
/// </summary>
public sealed class HelperDataDirectoryTests
{
    [Fact]
    public void TheHelpersOwnSecurityIsTrusted()
    {
        DirectorySecurity security = HelperDataDirectory.NewSecurity();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(HelperDataDirectory.IsSecure(security));
    }

    [Theory]
    [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)")]
    [InlineData("O:BAD:P(A;;FR;;;BA)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FR;;;WD)")] // everyone may read: can't change anything
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)")] // Users may read and list
    [InlineData("O:BAD:P(D;OICI;FA;;;BU)(A;OICI;FA;;;SY)")] // a deny only takes rights away
    public void OnlySystemAndAdministratorsCanChangeIt(string sddl) => Assert.True(HelperDataDirectory.IsSecure(FromSddl(sddl)));

    [Theory]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")] // not protected: ProgramData's entries flow in
    [InlineData("O:BUD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")] // owned by Users
    [InlineData("O:S-1-5-21-1-2-3-1001D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")] // owned by a user
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x116;;;BU)")] // Users may write
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x4;;;BU)")] // Users may append (or create files)
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;SD;;;BU)")] // Users may delete
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;WD;;;BU)")] // Users may change the permissions
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GA;;;IU)")] // generic rights
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;CO)")] // creators own what they make
    public void AnythingThatLetsOthersChangeItIsNotTrusted(string sddl) => Assert.False(HelperDataDirectory.IsSecure(FromSddl(sddl)));

    [Fact]
    public void AFolderSomeoneElseMadeIsMovedAsideNotUsed()
    {
        using var temp = new TempDirectory();
        string path = temp.File("BlindsDown");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "device-lock-journal.json"), "planted");

        DataDirectoryResult result = HelperDataDirectory.Prepare(path);

        // Moved aside in one piece, contents untouched (never deleted or "repaired" one by one).
        Assert.NotNull(result.MovedAside);
        Assert.StartsWith(path + ".untrusted-", result.MovedAside, StringComparison.Ordinal);
        Assert.Equal("planted", File.ReadAllText(Path.Combine(result.MovedAside, "device-lock-journal.json")));
        AssertFreshOrRefused(path, result);
    }

    [Fact]
    public void AFileWhereTheFolderShouldBeIsMovedAside()
    {
        using var temp = new TempDirectory();
        string path = temp.File("BlindsDown");
        File.WriteAllText(path, "not a folder");

        DataDirectoryResult result = HelperDataDirectory.Prepare(path);

        Assert.NotNull(result.MovedAside);
        Assert.Equal("not a folder", File.ReadAllText(result.MovedAside));
        AssertFreshOrRefused(path, result);
    }

    private static DirectorySecurity FromSddl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    /// <summary>Elevated, the new folder is created with the helper's own security. Unelevated, Windows doesn't let
    /// Administrators be the owner, so the folder is not created and the problem is reported (never thrown).</summary>
    private static void AssertFreshOrRefused(string path, DataDirectoryResult result)
    {
        if (ProcessElevation.IsElevated())
        {
            Assert.True(result.Ready, result.Problem);
            Assert.Empty(Directory.EnumerateFileSystemEntries(path));
            Assert.True(HelperDataDirectory.IsSecure(new DirectoryInfo(path).GetAccessControl()));
        }
        else
        {
            Assert.False(result.Ready);
            Assert.False(string.IsNullOrEmpty(result.Problem));
            Assert.False(Directory.Exists(path));
        }
    }
}
