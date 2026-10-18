using BlindsDown.Helper;

namespace BlindsDown.Tests.Helper;

public sealed class HelperPathsTests
{
    [Fact]
    public void ProgramLivesUnderProgramFiles()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.Equal(Path.Combine(programFiles, "BlindsDown"), HelperPaths.InstallDirectory);
        Assert.Equal(Path.Combine(programFiles, "BlindsDown", "BlindsDown.exe"), HelperPaths.InstalledExe);
        Assert.True(Path.IsPathFullyQualified(HelperPaths.InstalledExe));
    }

    [Fact]
    public void DataLivesUnderProgramData()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(Path.Combine(programData, "BlindsDown"), HelperPaths.DataDirectory);

        // All in the one protected folder, which removing the helper deletes as a whole.
        string[] files = [HelperPaths.JournalFile, HelperPaths.ChangeLogFile, HelperPaths.LogFile, HelperPaths.SetupLogFile];
        Assert.All(files, file => Assert.Equal(HelperPaths.DataDirectory, Path.GetDirectoryName(file)));
        Assert.Equal(files.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Path.Combine(programData, "BlindsDown", "lock-changes.json"), HelperPaths.ChangeLogFile);
    }

    [Fact]
    public void NothingIsUnderTheUsersProfile()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.All(
            new[] { HelperPaths.InstalledExe, HelperPaths.JournalFile, HelperPaths.ChangeLogFile, HelperPaths.LogFile, HelperPaths.SetupLogFile },
            path => Assert.False(path.StartsWith(profile, StringComparison.OrdinalIgnoreCase), path));
    }

    [Fact]
    public void ThePipeIsAlwaysOnThisComputer()
    {
        Assert.Equal(@"\\.\pipe\BlindsDown.Helper", HelperPaths.LocalPipePath("BlindsDown.Helper"));
        Assert.Equal(@"\\.\pipe\a-b_c.1", HelperPaths.LocalPipePath("a-b_c.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(@"..\BlindsDown.Helper")]
    [InlineData(@"\\server\pipe\BlindsDown.Helper")]
    [InlineData("pipe/x")]
    [InlineData("x:y")]
    [InlineData("x y")]
    [InlineData("x\0")]
    [InlineData("é")]
    public void UnsafePipeNamesAreRejected(string? name)
    {
        Assert.False(HelperPaths.IsValidPipeName(name));
        Assert.Throws<ArgumentException>(() => HelperPaths.LocalPipePath(name!));
    }

    [Fact]
    public void OverlongPipeNamesAreRejected() => Assert.False(HelperPaths.IsValidPipeName(new string('a', 201)));
}
