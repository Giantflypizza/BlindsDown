using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using BlindsDown.Helper;

namespace BlindsDown.Tests.Helper;

/// <summary>The pipe's access list and the client's "is this the real helper?" rule, checked as data.</summary>
public sealed class HelperPipeAclTests
{
    private const uint GenericAll = 0x10000000;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;

    [Fact]
    public void ServiceAccessListDeniesTheNetworkAndLimitsInteractiveUsersToReadAndWrite()
    {
        var descriptor = new RawSecurityDescriptor(HelperPipeAcl.ServiceSddl);

        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));

        // Owned by SYSTEM, explicitly: the tray app recognises the real helper by it.
        Assert.True(descriptor.Owner!.IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Equal("O:SY" + HelperPipeAcl.ServiceDacl, HelperPipeAcl.ServiceSddl);
        CommonAce[] aces = [.. descriptor.DiscretionaryAcl!.Cast<CommonAce>()];
        Assert.Collection(
            aces,
            ace => AssertAce(ace, AceQualifier.AccessDenied, WellKnownSidType.NetworkSid, GenericAll),
            ace => AssertAce(ace, AceQualifier.AccessAllowed, WellKnownSidType.LocalSystemSid, GenericAll),
            ace => AssertAce(ace, AceQualifier.AccessAllowed, WellKnownSidType.BuiltinAdministratorsSid, GenericAll),
            ace => AssertAce(ace, AceQualifier.AccessAllowed, WellKnownSidType.InteractiveSid, HelperPipeAcl.ClientAccessMask));

        // The deny comes first, so it wins over every allow.
        Assert.Equal(AceQualifier.AccessDenied, aces[0].AceQualifier);
    }

    [Fact]
    public void ClientAccessIsGenericReadAndWriteWithoutCreatingPipeInstances()
    {
        Assert.Equal((FileGenericRead | FileGenericWrite) & ~HelperPipeAcl.FileCreatePipeInstance, HelperPipeAcl.ClientAccessMask);
        Assert.Equal(0u, HelperPipeAcl.ClientAccessMask & HelperPipeAcl.FileCreatePipeInstance);
        Assert.Equal(0x0012019Bu, HelperPipeAcl.ClientAccessMask);
        Assert.Contains("0x0012019b", HelperPipeAcl.ServiceSddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestAccessListIsForTheCurrentUserOnlyAndStillDeniesTheNetwork()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();

        var descriptor = new RawSecurityDescriptor(HelperPipeAcl.TestSddl(me.User!));

        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        CommonAce[] aces = [.. descriptor.DiscretionaryAcl!.Cast<CommonAce>()];
        Assert.Equal(2, aces.Length);
        AssertAce(aces[0], AceQualifier.AccessDenied, WellKnownSidType.NetworkSid, GenericAll);
        Assert.Equal(AceQualifier.AccessAllowed, aces[1].AceQualifier);
        Assert.Equal(me.User, aces[1].SecurityIdentifier);
    }

    [Fact]
    public void WindowsAcceptsBothDescriptorsAndRejectsGarbage()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();

        using (NativeSecurityDescriptor service = NativeSecurityDescriptor.FromSddl(HelperPipeAcl.ServiceSddl))
        {
            Assert.False(service.IsInvalid);
        }

        using (NativeSecurityDescriptor test = NativeSecurityDescriptor.FromSddl(HelperPipeAcl.TestSddl(me.User!)))
        {
            Assert.False(test.IsInvalid);
        }

        Assert.Throws<Win32Exception>(() => NativeSecurityDescriptor.FromSddl("D:(not sddl"));
    }

    [Theory]
    [InlineData("S-1-5-18", 0u)]
    [InlineData("S-1-5-32-544", 0u)]
    public void TheRealHelperIsAccepted(string owner, uint session) => Assert.Null(HelperPipeAcl.ServerProblem(owner, session));

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", 0u)]
    [InlineData("S-1-5-21-1-2-3-1001", 1u)]
    [InlineData("S-1-5-19", 0u)]
    [InlineData("S-1-5-20", 0u)]
    [InlineData("S-1-5-32-545", 0u)]
    [InlineData("S-1-5-18", 1u)]
    [InlineData("S-1-5-32-544", 2u)]
    [InlineData("S-1-5-18", uint.MaxValue)]
    [InlineData(null, 0u)]
    public void AnythingElseIsRefused(string? owner, uint session) => Assert.NotNull(HelperPipeAcl.ServerProblem(owner, session));

    private static void AssertAce(CommonAce ace, AceQualifier qualifier, WellKnownSidType account, uint mask)
    {
        Assert.Equal(qualifier, ace.AceQualifier);
        Assert.True(ace.SecurityIdentifier.IsWellKnown(account), $"Expected {account}, got {ace.SecurityIdentifier}.");
        Assert.Equal(mask, unchecked((uint)ace.AccessMask));
        Assert.Equal(AceFlags.None, ace.AceFlags);
    }
}
