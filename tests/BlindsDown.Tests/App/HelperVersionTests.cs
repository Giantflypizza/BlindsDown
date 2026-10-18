namespace BlindsDown.Tests.App;

/// <summary>An app may only offer to update a helper that is OLDER than itself: replacing a newer SYSTEM helper with an
/// old copy would be a downgrade.</summary>
public sealed class HelperVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("0.9.0", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.1.0", "1.0.0", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.1.0-beta", "1.0.0", 1)]
    [InlineData("1.0.0+abc", "1.0.0", -1)] // Same numbers, different text: not the same build, so offer the update.
    [InlineData(null, "1.0.0", -1)]
    [InlineData("garbage", "1.0.0", -1)]
    public void Compares_the_helper_with_the_app(string? helper, string app, int expected) =>
        Assert.Equal(expected, HelperLink.CompareVersions(helper, app));
}
