using BlindsDown.Interop.SystemApis;
using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

public sealed class ImagePathsTests
{
    private static readonly IReadOnlyList<(string Drive, string Device)> Drives =
    [
        ("C:", @"\Device\HarddiskVolume3"),
        ("D:", @"\Device\CdRom0"),
        ("E:", @"\Device\HarddiskVolume30"),
        ("S:", @"\??\C:\Projects"),
        ("T:", @"\??\Z:\Team"),
        ("X:", @"\Device\RdpDr\;X:1\tsclient\X"),
        ("Z:", @"\Device\LanmanRedirector\;Z:000123\server\share"),
    ];

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\svchost.exe", @"C:\Windows\System32\svchost.exe")]
    [InlineData(@"\Device\HarddiskVolume30\Apps\a.exe", @"E:\Apps\a.exe")]
    [InlineData(@"\device\harddiskvolume3\x.exe", @"C:\x.exe")]
    [InlineData(@"\??\C:\Tools\t.exe", @"C:\Tools\t.exe")]
    [InlineData(@"\??\UNC\server\share\t.exe", @"\\server\share\t.exe")]
    [InlineData(@"\Device\Mup\server\share\t.exe", @"\\server\share\t.exe")]
    [InlineData(@"\SystemRoot\System32\smss.exe", @"C:\Windows\System32\smss.exe")]
    public void KernelPathsBecomeDosPaths(string ntPath, string expected) =>
        Assert.Equal(expected, ImagePaths.ToDosPath(ntPath, Drives, @"C:\Windows"));

    [Theory]
    [InlineData(@"\Device\HarddiskVolume9\x.exe")]
    [InlineData(@"\Device\HarddiskVolume3")]
    [InlineData("Registry")]
    public void UnmappableKernelPathsGiveNull(string ntPath) =>
        Assert.Null(ImagePaths.ToDosPath(ntPath, Drives, @"C:\Windows"));

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", true)]
    [InlineData(@"c:/Program Files/App/app.exe", true)]
    [InlineData(@"\\?\C:\App\app.exe", true)]
    [InlineData(@"D:\setup.exe", true)]
    [InlineData(@"S:\build\tool.exe", true)]
    [InlineData(@"T:\tool.exe", false)]
    [InlineData(@"X:\tool.exe", false)]
    [InlineData(@"Z:\tool.exe", false)]
    [InlineData(@"Q:\unmapped.exe", false)]
    [InlineData(@"\\server\share\tool.exe", false)]
    [InlineData(@"\\?\UNC\server\share\tool.exe", false)]
    [InlineData(@"//server/share/tool.exe", false)]
    [InlineData(@"\\.\pipe\something", false)]
    [InlineData(@"relative\tool.exe", false)]
    [InlineData(@"C:relative.exe", false)]
    [InlineData("", false)]
    public void OnlyLocalDiskPathsMayBeOpened(string path, bool expected) =>
        Assert.Equal(expected, ImagePaths.IsLocalDrivePath(path, Drives));

    [Theory]
    [InlineData(FileAttributes.Normal, true)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReadOnly, true)]
    [InlineData(FileAttributes.Offline, false)]
    [InlineData((FileAttributes)0x00040000, false)]
    [InlineData(FileAttributes.Archive | (FileAttributes)0x00400000, false)]
    public void FilesWhoseContentIsElsewhereAreNotOpened(FileAttributes attributes, bool expected) =>
        Assert.Equal(expected, ImagePaths.HasLocalContent(attributes));

    [Fact]
    public void OnlyExistingLocalFilesAreSafeToOpen()
    {
        string exe = SystemTestSupport.ProcessPath;

        Assert.True(ImagePaths.IsSafeToOpen(exe));
        Assert.False(ImagePaths.IsSafeToOpen(Path.Combine(Path.GetDirectoryName(exe)!, "blindsdown-missing.exe")));
        Assert.False(ImagePaths.IsSafeToOpen(@"\\blindsdown-test.invalid\share\tool.exe"));
    }

    [Fact]
    public void OwnImageResolvesThroughTheKernelPath()
    {
        string? ntPath = SystemQueries.GetImageNtPath(Environment.ProcessId);

        Assert.NotNull(ntPath);
        Assert.StartsWith(@"\Device\", ntPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SystemTestSupport.ProcessPath, ImagePaths.ToDosPath(ntPath), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemDriveIsLocal() =>
        Assert.True(ImagePaths.IsLocalDrivePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
}
