using System.Security.AccessControl;
using System.Security.Principal;
using BlindsDown.Core.Helper;
using BlindsDown.Helper;
using BlindsDown.Tests.Core.Fakes;
using Microsoft.Win32;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// The setup's building blocks that can be checked without administrator rights: the service command line, the
/// Installed apps entry, the copy plan, and the staged copy itself (into a temp folder, never Program Files).
/// Nothing here registers a service, writes HKLM or asks for elevation.
/// </summary>
public sealed class HelperSetupTests
{
    private const string Exe = @"C:\Program Files\BlindsDown\BlindsDown.exe";

    /// <summary>NT SERVICE\TrustedInstaller, the same on every PC.</summary>
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>The permissions of Windows' own Program Files folder.</summary>
    private const string ProgramFilesSddl =
        "O:" + TrustedInstaller + "G:" + TrustedInstaller
        + "D:PAI(A;OICIIO;GA;;;CO)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;BA)(A;;0x1301bf;;;BA)(A;OICIIO;GXGR;;;BU)"
        + "(A;;0x1200a9;;;BU)(A;CIIO;GA;;;" + TrustedInstaller + ")(A;;FA;;;" + TrustedInstaller + ")(A;;0x1200a9;;;AC)"
        + "(A;OICIIO;GXGR;;;AC)(A;;0x1200a9;;;S-1-15-2-2)(A;OICIIO;GXGR;;;S-1-15-2-2)";

    /// <summary>A folder an administrator made in Program Files: everything inherited from there.</summary>
    private const string InheritedFromProgramFilesSddl =
        "O:BAD:AI(A;ID;FA;;;BA)(A;OICIIOID;GA;;;CO)(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)(A;OICIIOID;GA;;;BA)(A;ID;0x1200a9;;;BU)"
        + "(A;OICIIOID;GXGR;;;BU)(A;ID;FA;;;" + TrustedInstaller + ")(A;CIIOID;GA;;;" + TrustedInstaller + ")(A;ID;0x1200a9;;;AC)"
        + "(A;OICIIOID;GXGR;;;AC)";

    /// <summary>Tests copy into temp folders, which belong to the user running them.</summary>
    private static readonly InstallFolderSecurity TestSecurity = InstallFolderSecurity.ForTests(CurrentUser());

    [Fact]
    public void ServiceCommandLineIsQuoted()
    {
        Assert.Equal("\"C:\\Program Files\\BlindsDown\\BlindsDown.exe\" --service", HelperServiceControl.BinaryPath(Exe));
    }

    [Theory]
    [InlineData(@"BlindsDown.exe")]
    [InlineData(@"Program Files\BlindsDown\BlindsDown.exe")]
    [InlineData("C:\\Program Files\\Blinds\"Down\\BlindsDown.exe")]
    public void ServiceCommandLineNeedsAFullPathWithoutQuotes(string exe) =>
        Assert.Throws<ArgumentException>(() => HelperServiceControl.BinaryPath(exe));

    [Theory]
    [InlineData("\"C:\\Program Files\\BlindsDown\\BlindsDown.exe\" --service", @"C:\Program Files\BlindsDown\BlindsDown.exe")]
    [InlineData("  \"C:\\Program Files\\BlindsDown\\BlindsDown.exe\"", @"C:\Program Files\BlindsDown\BlindsDown.exe")]
    [InlineData(@"C:\Program Files\BlindsDown\BlindsDown.exe --service", @"C:\Program Files\BlindsDown\BlindsDown.exe")]
    [InlineData(@"C:\Tools\helper.EXE", @"C:\Tools\helper.EXE")]
    [InlineData(@"C:\Tools\helper --service", @"C:\Tools\helper")]
    public void ExeIsFoundInAServiceCommandLine(string commandLine, string expected) =>
        Assert.Equal(expected, HelperServiceControl.ExePathFromBinaryPath(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    public void NoExeInAnEmptyCommandLine(string? commandLine) => Assert.Null(HelperServiceControl.ExePathFromBinaryPath(commandLine));

    [Fact]
    public void CommandLineRoundTrips() =>
        Assert.Equal(Exe, HelperServiceControl.ExePathFromBinaryPath(HelperServiceControl.BinaryPath(Exe)));

    [Fact]
    public void ServiceRestartsThreeTimesThenGivesUp()
    {
        Assert.Equal(4, HelperServiceControl.FailureActions.Length);
        Assert.All(HelperServiceControl.FailureActions[..3], a => Assert.Equal((1, 5000u), a));
        Assert.Equal((0, 0u), HelperServiceControl.FailureActions[3]);
        Assert.Equal(86400u, HelperServiceControl.FailureResetPeriodSeconds);
    }

    [Fact]
    public void ServiceDescriptionSpeaksAsBlindsDown()
    {
        string text = HelperServiceControl.Description;

        Assert.Contains("BlindsDown", text, StringComparison.Ordinal);
        Assert.Contains("never connects to the internet", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(we|us|our)\b", text.ToLowerInvariant());
    }

    [Theory]
    [InlineData("0.2.0+3f1c2ab9", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData(" 1.0.0-beta.1+abc ", "1.0.0-beta.1")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("+abc", null)]
    public void VersionTextDropsTheSourceControlSuffix(string? productVersion, string? expected) =>
        Assert.Equal(expected, HelperSetup.VersionText(productVersion));

    [Fact]
    public void QueryOnlyReadsAndNeverThrows()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Set BLINDSDOWN_DEVICE_TESTS=1 to run tests that read this machine's services.");

        HelperInstallInfo info = HelperSetup.Query();

        if (!info.Installed)
        {
            Assert.False(info.Running);
            Assert.Null(info.InstalledVersion);
        }
    }

    [Fact]
    public void ExitCodesAreTheDocumentedOnes()
    {
        Assert.Equal(0, HelperSetup.ExitOk);
        Assert.Equal(1, HelperSetup.ExitFailed);
        Assert.Equal(2, HelperSetup.ExitNotElevated);
        Assert.Equal(3, HelperSetup.ExitDevicesNotRestored);
    }

    [Fact]
    public void InstalledAppsEntryDescribesTheHelper()
    {
        UninstallEntry entry = UninstallEntry.For(Exe, "0.2.0", totalBytes: 50 * 1024 * 1024 + 1);

        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BlindsDownHelper", UninstallEntry.KeyPath);
        var values = entry.Values().ToDictionary(v => v.Name, v => (v.Value, v.Kind));
        Assert.Equal(("BlindsDown camera helper", RegistryValueKind.String), values["DisplayName"]);
        Assert.Equal(("0.2.0", RegistryValueKind.String), values["DisplayVersion"]);
        Assert.Equal(("BlindsDown", RegistryValueKind.String), values["Publisher"]);
        Assert.Equal((Exe + ",0", RegistryValueKind.String), values["DisplayIcon"]);
        Assert.Equal(("\"" + Exe + "\" --uninstall-helper", RegistryValueKind.String), values["UninstallString"]);
        Assert.Equal(("\"" + Exe + "\" --uninstall-helper", RegistryValueKind.String), values["QuietUninstallString"]);
        Assert.Equal((@"C:\Program Files\BlindsDown", RegistryValueKind.String), values["InstallLocation"]);
        Assert.Equal((1, RegistryValueKind.DWord), values["NoModify"]);
        Assert.Equal((1, RegistryValueKind.DWord), values["NoRepair"]);
        Assert.Equal((51201, RegistryValueKind.DWord), values["EstimatedSize"]);
        Assert.Equal(HelperProtocol.ServiceDisplayName, entry.DisplayName);
    }

    [Fact]
    public void InstalledAppsEntryNeverContainsAUrl()
    {
        UninstallEntry entry = UninstallEntry.For(Exe, "0.2.0", 1);

        Assert.All(entry.Values(), v => Assert.DoesNotContain("://", v.Value.ToString()!, StringComparison.Ordinal));
    }

    [Fact]
    public void SingleFileBuildCopiesOnlyTheExeUnderItsInstalledName()
    {
        IReadOnlyList<CopyItem> plan = HelperFiles.BuildPlan(
            @"C:\Users\me\Downloads\BlindsDown (1).exe",
            @"C:\Users\me\Downloads\",
            singleFile: true,
            [@"C:\Users\me\Downloads\BlindsDown.dll", @"C:\Users\me\Downloads\setup.exe"],
            []);

        CopyItem only = Assert.Single(plan);
        Assert.Equal(new CopyItem(@"C:\Users\me\Downloads\BlindsDown (1).exe", "BlindsDown.exe"), only);
    }

    [Fact]
    public void DevelopmentBuildCopiesTheProgramFilesOfTheAppFolder()
    {
        const string Base = @"C:\src\BlindsDown\bin\Debug\net10.0-windows\";
        IReadOnlyList<CopyItem> plan = HelperFiles.BuildPlan(
            Base + "BlindsDown.exe",
            Base,
            singleFile: false,
            [
                Base + "BlindsDown.exe",
                Base + "BlindsDown.dll",
                Base + "BlindsDown.Core.dll",
                Base + "BlindsDown.pdb",
                Base + "BlindsDown.deps.json",
                Base + "BlindsDown.runtimeconfig.json",
                Base + "System.ServiceProcess.ServiceController.dll",
                Base + "blindsdown.log",
                Base + "notes.txt",
                Base + "desktop.ini",
            ],
            [Base + @"runtimes\win\lib\net10.0\System.ServiceProcess.ServiceController.dll"]);

        Assert.Equal(
            [
                "BlindsDown.exe",
                "BlindsDown.dll",
                "BlindsDown.Core.dll",
                "BlindsDown.pdb",
                "BlindsDown.deps.json",
                "BlindsDown.runtimeconfig.json",
                "System.ServiceProcess.ServiceController.dll",
                @"runtimes\win\lib\net10.0\System.ServiceProcess.ServiceController.dll",
            ],
            plan.Select(p => p.RelativeTarget));
        Assert.Equal(Base + "BlindsDown.exe", plan[0].Source);
    }

    [Fact]
    public void DevelopmentBuildWithARenamedExeStillInstallsBlindsDownExe()
    {
        const string Base = @"C:\dev\";
        IReadOnlyList<CopyItem> plan = HelperFiles.BuildPlan(
            Base + "Renamed.exe",
            Base,
            singleFile: false,
            [Base + "Renamed.exe", Base + "BlindsDown.exe", Base + "BlindsDown.dll"],
            []);

        Assert.Equal([("BlindsDown.exe", Base + "Renamed.exe"), ("BlindsDown.dll", Base + "BlindsDown.dll")], plan.Select(p => (p.RelativeTarget, p.Source)));
    }

    [Fact]
    public void FilesOutsideTheAppFolderAreNeverCopied()
    {
        const string Base = @"C:\dev\app\";
        IReadOnlyList<CopyItem> plan = HelperFiles.BuildPlan(
            Base + "BlindsDown.exe",
            Base,
            singleFile: false,
            [@"C:\dev\other.dll", @"D:\elsewhere\x.dll"],
            []);

        Assert.Single(plan);
    }

    [Theory]
    [InlineData("BlindsDown.dll", true)]
    [InlineData(@"runtimes\win\lib\x.dll", true)]
    [InlineData(@"..\x.dll", false)]
    [InlineData(@"a\..\..\x.dll", false)]
    [InlineData(@"C:\x.dll", false)]
    [InlineData(@"\x.dll", false)]
    [InlineData(@".\x.dll", false)]
    [InlineData("", false)]
    public void OnlyPathsInsideTheFolderAreAccepted(string relative, bool expected) =>
        Assert.Equal(expected, HelperFiles.IsInsideFolder(relative));

    [Fact]
    public void StagedInstallCopiesEverythingIntoANewFolder()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"), (@"runtimes\win\x.dll", "new dll"));
        string install = Path.Combine(temp.Path, "install");

        StagedInstall files = StagedInstall.Apply(plan, install, TestSecurity);
        files.Commit();

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(install, @"runtimes\win\x.dll")));

        // The marker says setup made this folder, so removing the helper may remove all of it.
        Assert.Equal([HelperFiles.FolderMarkerName, "BlindsDown.exe", @"runtimes\win\x.dll"], FilesIn(install));
    }

    [Fact]
    public void RemovingTheHelperRemovesTheFolderSetupMade()
    {
        using var temp = new TempDirectory();
        string install = temp.File("install");
        StagedInstall.Apply(MakeSource(temp, ("BlindsDown.exe", "new exe"), (@"runtimes\x.dll", "dll")), install, TestSecurity).Commit();

        Assert.True(HelperFiles.RemoveInstallFolder(install));

        Assert.False(Directory.Exists(install));
        Assert.True(HelperFiles.RemoveInstallFolder(install));
    }

    [Fact]
    public void RemovingTheHelperKeepsAFolderThatWasThereBefore()
    {
        // E.g. the user unzipped BlindsDown into Program Files and runs it from there: removing the helper must not
        // take the user's own copy with it. Only setup's own leftovers go.
        using var temp = new TempDirectory();
        string install = temp.File("install");
        MakeOwnFolder(install);
        Directory.CreateDirectory(Path.Combine(install, @"runtimes\win"));
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "the user's exe");
        File.WriteAllText(Path.Combine(install, "README.md"), "the user's readme");
        StagedInstall.Apply(MakeSource(temp, ("BlindsDown.exe", "new exe")), install, TestSecurity).Commit();
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe.old-1a2b3c4d"), "left by an update");
        File.WriteAllText(Path.Combine(install, @"runtimes\win\x.dll.undo-89abcdef"), "left by an undo");
        Directory.CreateDirectory(Path.Combine(install, @".setup-0123abcd\runtimes"));
        File.WriteAllText(Path.Combine(install, @".setup-0123abcd\runtimes\y.dll"), "half a copy");

        Assert.True(HelperFiles.RemoveInstallFolder(install));

        Assert.Equal(["BlindsDown.exe", "README.md"], FilesIn(install));
        Assert.Equal("the user's readme", File.ReadAllText(Path.Combine(install, "README.md")));
    }

    [Theory]
    [InlineData("BlindsDown.exe.old-1a2b3c4d", false, true)]
    [InlineData("x.dll.undo-89abcdef", false, true)]
    [InlineData(".setup-0123abcd", true, true)]
    [InlineData("BlindsDown.exe", false, false)]
    [InlineData("notes.old-1a2b3c4d.txt", false, false)]
    [InlineData("x.old-1A2B3C4D", false, false)]
    [InlineData("x.old-1a2b3c", false, false)]
    [InlineData(".setup-0123abcd", false, false)]
    [InlineData("BlindsDown.exe.old-1a2b3c4d", true, false)]
    [InlineData(".setup-0123abcd-mine", true, false)]
    [InlineData("runtimes", true, false)]
    public void OnlySetupsOwnNamesCountAsItsLeftovers(string name, bool isDirectory, bool expected) =>
        Assert.Equal(expected, HelperFiles.IsSetupLeftover(name, isDirectory));

    [Fact]
    public void StagedInstallReplacesOldFilesAndCommitDeletesThem()
    {
        using var temp = new TempDirectory();
        string install = Path.Combine(temp.Path, "install");
        MakeOwnFolder(install);
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "keep.txt"), "not ours to touch");
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"));

        StagedInstall files = StagedInstall.Apply(plan, install, TestSecurity);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
        Assert.Contains(FilesIn(install), f => f.StartsWith("BlindsDown.exe.old-", StringComparison.Ordinal));

        files.Commit();
        Assert.Equal(["BlindsDown.exe", "keep.txt"], FilesIn(install));
    }

    [Fact]
    public void UndoPutsTheOldFilesBack()
    {
        using var temp = new TempDirectory();
        string install = Path.Combine(temp.Path, "install");
        MakeOwnFolder(install);
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "old exe");
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"), ("BlindsDown.dll", "new dll"));

        StagedInstall.Apply(plan, install, TestSecurity).Undo();

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
        Assert.Equal(["BlindsDown.exe"], FilesIn(install));
    }

    [Fact]
    public void UndoOfAFreshInstallRemovesTheFolder()
    {
        using var temp = new TempDirectory();
        string install = Path.Combine(temp.Path, "install");
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"), (@"runtimes\x.dll", "dll"));

        StagedInstall.Apply(plan, install, TestSecurity).Undo();

        Assert.False(Directory.Exists(install));
    }

    [Fact]
    public void FailedCopyLeavesTheOldInstallUntouched()
    {
        using var temp = new TempDirectory();
        string install = Path.Combine(temp.Path, "install");
        MakeOwnFolder(install);
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "old exe");
        List<CopyItem> plan = [.. MakeSource(temp, ("BlindsDown.exe", "new exe")), new CopyItem(Path.Combine(temp.Path, "missing.dll"), "missing.dll")];

        Assert.Throws<FileNotFoundException>(() => StagedInstall.Apply(plan, install, TestSecurity));

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
        Assert.Equal(["BlindsDown.exe"], FilesIn(install));
    }

    [Fact]
    public void FilesThatAlreadyAreTheInstalledOnesAreNotCopied()
    {
        // BlindsDown running from the install folder itself: nothing to copy, nothing to break.
        using var temp = new TempDirectory();
        string install = Path.Combine(temp.Path, "install");
        MakeOwnFolder(install);
        string exe = Path.Combine(install, "BlindsDown.exe");
        File.WriteAllText(exe, "running exe");

        StagedInstall files = StagedInstall.Apply([new CopyItem(exe, "BlindsDown.exe")], install, TestSecurity);
        files.Commit();

        Assert.Equal("running exe", File.ReadAllText(exe));
        Assert.Equal(["BlindsDown.exe"], FilesIn(install));
    }

    [Fact]
    public void RemoveTreeDeletesAFolderWithEverythingInIt()
    {
        using var temp = new TempDirectory();
        string folder = Path.Combine(temp.Path, "remove-me");
        Directory.CreateDirectory(Path.Combine(folder, @"a\b"));
        File.WriteAllText(Path.Combine(folder, "x.dll"), "x");
        File.WriteAllText(Path.Combine(folder, @"a\b\y.dll"), "y");

        Assert.True(HelperFiles.RemoveTree(folder));
        Assert.False(Directory.Exists(folder));
        Assert.True(HelperFiles.RemoveTree(folder));
    }

    [Fact]
    public void RemoveTreeDeletesALinkButNeverWhatItPointsTo()
    {
        // Setup deletes folders as an administrator; .NET's own recursive listing would walk into the junction and
        // delete the files it points to.
        using var temp = new TempDirectory();
        string outside = MakeOutsideFolder(temp);
        string folder = temp.File("remove-me");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, @"sub\x.dll"), "x");
        using TestJunction junction = TestJunction.Create(Path.Combine(folder, @"sub\link"), outside);

        Assert.True(HelperFiles.RemoveTree(folder));

        Assert.False(Directory.Exists(folder));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void RemoveTreeOfALinkRemovesOnlyTheLink()
    {
        using var temp = new TempDirectory();
        string outside = MakeOutsideFolder(temp);
        string link = temp.File("install");
        using TestJunction junction = TestJunction.Create(link, outside);

        Assert.True(HelperFiles.RemoveTree(link));

        Assert.False(Directory.Exists(link));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void RemoveTreeAlsoTakesAFileWhereTheFolderWas()
    {
        using var temp = new TempDirectory();
        string file = temp.File("not-a-folder");
        File.WriteAllText(file, "x");

        Assert.True(HelperFiles.RemoveTree(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void StagedInstallRefusesAnInstallFolderThatIsALink()
    {
        // A junction in place of the install folder would put the program that runs as SYSTEM wherever it points.
        using var temp = new TempDirectory();
        string outside = MakeOutsideFolder(temp);
        string install = temp.File("install");
        using TestJunction junction = TestJunction.Create(install, outside);
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"));

        Assert.Throws<IOException>(() => StagedInstall.Apply(plan, install));

        Assert.Equal(["precious.txt"], FilesIn(outside));
    }

    [Theory]
    [InlineData(ProgramFilesSddl)]
    [InlineData(InheritedFromProgramFilesSddl)]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)")] // what setup gives a folder it makes
    [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(D;OICI;FA;;;WD)")] // a deny only takes rights away
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FR;;;WD)")] // everyone may read
    public void FoldersOnlyAdministratorsCanChangeAreTrusted(string sddl) =>
        Assert.True(HelperFiles.OnlyTheseCanChange(FromSddl(sddl), HelperFiles.IsAdministrativeAccount));

    [Theory]
    [InlineData("O:S-1-5-21-1-2-3-1001D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")] // owned by a user, who can rewrite the permissions
    [InlineData("O:BUD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")] // owned by Users
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;BU)")] // Users may modify
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;AU)")] // Authenticated Users may modify
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;S-1-5-21-1-2-3-1001)")] // a user has full control (a folder moved from the user's own folders)
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;;0x2;;;WD)")] // everyone may add files (a DLL next to the exe)
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICIIO;GW;;;BU)")] // Users may change what is put inside
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICIIO;GA;;;BU)")] // the same, with full control
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;WD;;;BU)")] // Users may change the permissions
    [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;CO)")] // not only for what is made inside
    public void FoldersOthersCanChangeAreNotTrusted(string sddl) =>
        Assert.False(HelperFiles.OnlyTheseCanChange(FromSddl(sddl), HelperFiles.IsAdministrativeAccount));

    [Fact]
    public void ThisPcsProgramFilesIsTrustedAndATempFolderIsNot()
    {
        // Read-only: only the permissions are read.
        const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        using var temp = new TempDirectory();

        Assert.True(HelperFiles.OnlyTheseCanChange(new DirectoryInfo(programFiles).GetAccessControl(Sections), HelperFiles.IsAdministrativeAccount));
        Assert.False(HelperFiles.OnlyTheseCanChange(new DirectoryInfo(temp.Path).GetAccessControl(Sections), HelperFiles.IsAdministrativeAccount));
    }

    [Fact]
    public void AnInstallFolderThatWasThereAndOthersCanChangeIsRefused()
    {
        // E.g. BlindsDown moved from the user's own folder into Program Files: the move keeps the folder's
        // permissions, so its user could still replace what the SYSTEM service runs.
        using var temp = new TempDirectory();
        string install = temp.File("install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "the user's exe");
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"));

        IOException error = Assert.Throws<IOException>(() => StagedInstall.Apply(plan, install));

        Assert.Contains("administrators", error.Message, StringComparison.Ordinal);
        Assert.Equal(["BlindsDown.exe"], FilesIn(install));
        Assert.Equal("the user's exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
        Assert.Empty(Directory.EnumerateDirectories(install));
    }

    [Fact]
    public void AnInstallFolderEveryoneMayWriteToIsRefused()
    {
        using var temp = new TempDirectory();
        string install = temp.File("install");
        MakeFolderEveryoneMayChange(install);
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"));

        Assert.Throws<IOException>(() => StagedInstall.Apply(plan, install, TestSecurity));

        Assert.Empty(FilesIn(install));
    }

    [Fact]
    public void ASubfolderOthersCanChangeIsRefused()
    {
        // A development build puts DLLs under runtimes\: whoever can change that folder can swap them.
        using var temp = new TempDirectory();
        string install = temp.File("install");
        MakeOwnFolder(install);
        File.WriteAllText(Path.Combine(install, "BlindsDown.exe"), "old exe");
        MakeFolderEveryoneMayChange(Path.Combine(install, "runtimes"));
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"), (@"runtimes\win\x.dll", "new dll"));

        Assert.Throws<IOException>(() => StagedInstall.Apply(plan, install, TestSecurity));

        Assert.Equal(["BlindsDown.exe"], FilesIn(install));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "BlindsDown.exe")));
    }

    [Fact]
    public void ASubfolderThatIsALinkIsRefused()
    {
        using var temp = new TempDirectory();
        string outside = MakeOutsideFolder(temp);
        string install = temp.File("install");
        MakeOwnFolder(install);
        using TestJunction junction = TestJunction.Create(Path.Combine(install, "runtimes"), outside);
        IReadOnlyList<CopyItem> plan = MakeSource(temp, ("BlindsDown.exe", "new exe"), (@"runtimes\x.dll", "new dll"));

        Assert.Throws<IOException>(() => StagedInstall.Apply(plan, install, TestSecurity));

        Assert.Equal(["precious.txt"], FilesIn(outside));
        Assert.False(File.Exists(Path.Combine(install, "BlindsDown.exe")));
    }

    [Fact]
    public void AnExeUsedAsItIsMustBeSafeToo()
    {
        // BlindsDown running from the install folder: nothing is copied, so the exe's own permissions decide.
        using var temp = new TempDirectory();
        string install = temp.File("install");
        MakeOwnFolder(install);
        string exe = Path.Combine(install, "BlindsDown.exe");
        File.WriteAllText(exe, "running exe");
        FileSecurity weak = new FileInfo(exe).GetAccessControl();
        weak.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(exe).SetAccessControl(weak);

        Assert.Throws<IOException>(() => StagedInstall.Apply([new CopyItem(exe, "BlindsDown.exe")], install, TestSecurity));

        Assert.Equal("running exe", File.ReadAllText(exe));
    }

    [Fact]
    public void SetupsOwnFolderIsTrustedWhenItIsUpdated()
    {
        // Otherwise every update after the first install would be refused.
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(InstallFolderSecurity.AdministratorsOnly.NewFolderSddl);

        Assert.True(HelperFiles.OnlyTheseCanChange(security, InstallFolderSecurity.AdministratorsOnly.MayChange));
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), security.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void TotalBytesAddsUpEveryFile()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("sub"));
        File.WriteAllBytes(temp.File("a"), new byte[100]);
        File.WriteAllBytes(temp.File(@"sub\b"), new byte[23]);

        Assert.Equal(123, HelperFiles.TotalBytes(temp.Path));
        Assert.Equal(0, HelperFiles.TotalBytes(temp.File("missing")));
    }

    [Fact]
    public void TotalBytesDoesNotCountWhatALinkPointsTo()
    {
        using var temp = new TempDirectory();
        string outside = MakeOutsideFolder(temp);
        string folder = temp.File("install");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a"), new byte[10]);
        using TestJunction junction = TestJunction.Create(Path.Combine(folder, "link"), outside);

        Assert.Equal(10, HelperFiles.TotalBytes(folder));
        Assert.Equal(0, HelperFiles.TotalBytes(Path.Combine(folder, "link")));
    }

    [Fact]
    public void DevelopmentBuildNeverCopiesADevRuntimeConfig()
    {
        // It would make .NET load code from the user's own NuGet folder into a SYSTEM service.
        const string Base = @"C:\dev\";
        IReadOnlyList<CopyItem> plan = HelperFiles.BuildPlan(
            Base + "BlindsDown.exe",
            Base,
            singleFile: false,
            [Base + "BlindsDown.runtimeconfig.json", Base + "BlindsDown.runtimeconfig.dev.json", Base + "BlindsDown.Runtimeconfig.DEV.json"],
            []);

        Assert.Equal(["BlindsDown.exe", "BlindsDown.runtimeconfig.json"], plan.Select(p => p.RelativeTarget));
    }

    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        return me.User!;
    }

    private static DirectorySecurity FromSddl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    /// <summary>A folder only this user (standing in for administrators) and SYSTEM and Administrators can change, as
    /// setup makes one; nothing inherited from the temp folder, where other accounts may have rights.</summary>
    private static void MakeOwnFolder(string path) => new DirectoryInfo(path).Create(FromSddl(TestSecurity.NewFolderSddl));

    /// <summary>A folder owned by this user (so the test can delete it) that also lets everyone change it.</summary>
    private static void MakeFolderEveryoneMayChange(string path)
    {
        string user = CurrentUser().Value;
        new DirectoryInfo(path).Create(FromSddl($"O:{user}D:P(A;OICI;FA;;;{user})(A;OICI;0x1301bf;;;WD)"));
    }

    private static string MakeOutsideFolder(TempDirectory temp)
    {
        string outside = temp.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        return outside;
    }

    private static IReadOnlyList<CopyItem> MakeSource(TempDirectory temp, params (string Relative, string Content)[] files)
    {
        var plan = new List<CopyItem>();
        foreach ((string relative, string content) in files)
        {
            string source = Path.Combine(temp.Path, "source", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, content);
            plan.Add(new CopyItem(source, relative));
        }

        return plan;
    }

    private static List<string> FilesIn(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal)];
}
