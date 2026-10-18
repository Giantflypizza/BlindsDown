using System.Text.RegularExpressions;

namespace BlindsDown.Tests.Dialogs;

/// <summary>
/// The app speaks as BlindsDown, and where its developer speaks, as one person ("me", "I"). So no text shown
/// to the user may say "we", "us" or "our". Checked on every string literal in the UI's source, so a new
/// window can't slip one in.
/// </summary>
public partial class WordingTests
{
    [Fact]
    public void NoUserFacingTextSaysWeUsOrOur()
    {
        string ui = Path.Combine(RepositoryRoot(), "src", "BlindsDown", "Ui");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                string code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match literal in StringLiteral().Matches(code))
                {
                    if (FirstPersonPlural().IsMatch(literal.Value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {literal.Value}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("\"We muted it\"", true)]
    [InlineData("\"Let us know\"", true)]
    [InlineData("\"our helper\"", true)]
    [InlineData("\"USB microphones\"", false)]
    [InlineData("\"BlindsDown muted it\"", false)]
    [InlineData("\"Lock your cameras too\"", false)]
    public void TheCheckFindsFirstPersonPluralOnly(string literal, bool found) =>
        Assert.Equal(found, FirstPersonPlural().IsMatch(literal));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlindsDown.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(we|us|our|ours|we're|we've|we'll)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPersonPlural();
}
