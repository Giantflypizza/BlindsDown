using System.Text;
using BlindsDown.Core.Usage;
using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

public sealed class AppIdentityRulesTests
{
    [Fact]
    public void UnknownNamesThePid()
    {
        AppIdentity unknown = AppIdentityRules.Unknown(1234);

        Assert.Equal("pid:1234", unknown.Key);
        Assert.Equal("Unknown app (PID 1234)", unknown.DisplayName);
        Assert.Null(unknown.ExePath);
        Assert.Null(unknown.PackageFamilyName);
    }

    [Theory]
    [InlineData(" ", "SteelSeries Sonar", "SteelSeries Sonar")]
    [InlineData("Discord", "Discord", "Discord")]
    [InlineData("  Spaced  ", null, "Spaced")]
    [InlineData(null, "  ", "SteelSeriesSonar")]
    [InlineData("", null, "SteelSeriesSonar")]
    public void DesktopNamePrefersDescriptionThenProductThenFileName(string? description, string? product, string expected) =>
        Assert.Equal(expected, AppIdentityRules.ChooseDesktopName(description, product, @"C:\Program Files\SteelSeries\SteelSeriesSonar.exe"));

    [Fact]
    public void DesktopKeyIsTheFullPathLowerCase()
    {
        AppIdentity app = AppIdentityRules.ForExePath(@"C:\Apps\Folder\..\Missing App.EXE");

        Assert.Equal(@"c:\apps\missing app.exe", app.Key);
        Assert.Equal("Missing App", app.DisplayName);
        Assert.Null(app.PackageFamilyName);
    }

    [Fact]
    public void NetworkPathsAreNamedFromTheFileNameOnly()
    {
        // The share doesn't exist; the point is that only the name is used (nothing is opened).
        AppIdentity app = AppIdentityRules.ForExePath(@"\\blindsdown-test.invalid\share\Remote Tool.exe");

        Assert.Equal("Remote Tool", app.DisplayName);
        Assert.Equal(@"\\blindsdown-test.invalid\share\remote tool.exe", app.Key);
    }

    [Fact]
    public void PackageKeyIsTheFamilyAndUninstalledPackagesGetAPrettifiedName()
    {
        AppIdentity app = AppIdentityRules.ForPackage("Contoso.SoundRecorderPro_abc123xyz");

        Assert.Equal("Contoso.SoundRecorderPro_abc123xyz", app.Key);
        Assert.Equal("Contoso.SoundRecorderPro_abc123xyz", app.PackageFamilyName);
        Assert.Equal("Sound Recorder Pro", app.DisplayName);
        Assert.Equal("pkg:CONTOSO.SOUNDRECORDERPRO_ABC123XYZ", app.GroupKey);
    }

    [Theory]
    [InlineData("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", "Windows Sound Recorder")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "MS Teams")]
    [InlineData("A1B2C3D4.SampleMixer_abcdefghjk12", "Sample Mixer")]
    [InlineData("Fabrikam_abcdefghjk12", "Fabrikam")]
    [InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy", "Immersivecontrolpanel")]
    [InlineData("Contoso.voice-notes_abc", "Voice notes")]
    public void FamilyNamesArePrettified(string family, string expected) =>
        Assert.Equal(expected, AppIdentityRules.PrettifyFamilyName(family));

    [Fact]
    public void ManifestDisplayNameIsReadFromPackageProperties()
    {
        const string Manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="urn:test:foundation" xmlns:uap="urn:test:uap">
              <Identity Name="Contoso.App" Version="1.0.0.0" />
              <Applications>
                <Application Id="App"><uap:VisualElements DisplayName="Not this one" /></Application>
              </Applications>
              <Properties>
                <PublisherDisplayName>Contoso</PublisherDisplayName>
                <DisplayName> Contoso Recorder </DisplayName>
              </Properties>
            </Package>
            """;

        Assert.Equal("Contoso Recorder", ReadManifest(Manifest));
    }

    [Fact]
    public void ManifestResourceReferencesAreReturnedForResolution()
    {
        const string Manifest = """
            <Package><Properties><DisplayName>ms-resource:AppStoreName</DisplayName></Properties></Package>
            """;

        Assert.Equal("ms-resource:AppStoreName", ReadManifest(Manifest));
    }

    [Fact]
    public void ManifestsWithADtdAreRejected()
    {
        const string Manifest = """
            <?xml version="1.0"?>
            <!DOCTYPE Package [ <!ENTITY name "Injected"> ]>
            <Package><Properties><DisplayName>&name;</DisplayName></Properties></Package>
            """;

        Assert.Null(ReadManifest(Manifest));
    }

    [Theory]
    [InlineData("<Package><Properties /></Package>")]
    [InlineData("<Package><Properties><DisplayName>   </DisplayName></Properties></Package>")]
    [InlineData("not xml at all")]
    [InlineData("<Package><Other><DisplayName>Nested elsewhere</DisplayName></Other></Package>")]
    public void ManifestsWithoutADisplayNameGiveNull(string manifest) => Assert.Null(ReadManifest(manifest));

    [Fact]
    public void SimpleResourceKeysTryResourcesFolderFirst()
    {
        IReadOnlyList<string> candidates = AppIdentityRules.IndirectStringCandidates(
            "ms-resource:AppName", "Contoso.App_1.2.3.0_x64__abc123");

        Assert.Equal(
            [
                "@{Contoso.App_1.2.3.0_x64__abc123?ms-resource://Contoso.App/Resources/AppName}",
                "@{Contoso.App_1.2.3.0_x64__abc123?ms-resource:///Resources/AppName}",
                "@{Contoso.App_1.2.3.0_x64__abc123?ms-resource://Contoso.App/AppName}",
            ],
            candidates);
    }

    [Fact]
    public void ResourcePathsAreUsedAsGiven()
    {
        IReadOnlyList<string> candidates = AppIdentityRules.IndirectStringCandidates(
            "ms-resource:/Resources/AppTitle", "Contoso.App_1.0.0.0_neutral__abc");

        Assert.Equal("@{Contoso.App_1.0.0.0_neutral__abc?ms-resource://Contoso.App/Resources/AppTitle}", candidates[0]);
        Assert.Equal("@{Contoso.App_1.0.0.0_neutral__abc?ms-resource:///Resources/AppTitle}", candidates[1]);
    }

    [Fact]
    public void FullResourceUrisAreUsedVerbatim()
    {
        IReadOnlyList<string> candidates = AppIdentityRules.IndirectStringCandidates(
            "ms-resource://Contoso.App/Resources/Title", "Contoso.App_1.0.0.0_x64__abc");

        Assert.Equal(["@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/Resources/Title}"], candidates);
    }

    [Theory]
    [InlineData("Plain Name")]
    [InlineData("ms-resource:")]
    [InlineData("ms-resource:///")]
    public void NonResourceOrEmptyReferencesGiveNoCandidates(string value) =>
        Assert.Empty(AppIdentityRules.IndirectStringCandidates(value, "Contoso.App_1.0.0.0_x64__abc"));

    [Theory]
    [InlineData(@"ms-resource:x}@{\\blindsdown-test.invalid\share\r.pri?ms-resource://a/b")]
    [InlineData(@"ms-resource:\\blindsdown-test.invalid\share\AppName")]
    [InlineData("ms-resource:AppName?x")]
    [InlineData("ms-resource:App{Name}")]
    [InlineData("ms-resource:@AppName")]
    [InlineData("ms-resource:App\u0000Name")]
    public void ReferencesThatCouldRedirectTheLookupGiveNoCandidates(string value) =>
        Assert.Empty(AppIdentityRules.IndirectStringCandidates(value, "Contoso.App_1.0.0.0_x64__abc"));

    private static string? ReadManifest(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return AppIdentityRules.ReadManifestDisplayName(stream);
    }
}
