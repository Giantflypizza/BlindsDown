using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.App;

/// <summary>What a launch hands to a running BlindsDown, and which launches take commands themselves.</summary>
public sealed class LaunchPlanTests
{
    [Theory]
    [InlineData("", "Show", true)]
    [InlineData("--show", "Show", true)]
    [InlineData("--autostart", "Show", true)]
    [InlineData("--toggle", "Toggle", true)]
    [InlineData("--lock", "Lock", true)]
    [InlineData("--unlock", "Unlock", true)]
    [InlineData("--restore", "Unlock", false)] // Hands over an unlock; alone it restores and exits.
    [InlineData("--selftest", null, false)]
    [InlineData("--snapshot", null, false)]
    [InlineData("--nonsense", null, false)]
    public void Plans_each_command(string command, string? forward, bool runsTrayApp)
    {
        (InstanceCommand? Forward, bool RunsTrayApp) plan = BlindsDown.Program.Plan(command);

        Assert.Equal(forward, plan.Forward?.ToString());
        Assert.Equal(runsTrayApp, plan.RunsTrayApp);
    }

    [Theory]
    [InlineData("", "BlindsDown didn't start: ")]
    [InlineData("--autostart", "BlindsDown didn't start: ")]
    [InlineData("--lock", "BlindsDown didn't lock: ")]
    [InlineData("--unlock", "BlindsDown didn't unlock: ")]
    [InlineData("--toggle", "BlindsDown didn't lock or unlock: ")]
    [InlineData("--restore", "BlindsDown didn't restore anything: ")]
    public void Says_plainly_what_did_not_happen(string command, string start)
    {
        string text = BlindsDown.Program.NotTakenText(command);

        Assert.StartsWith(start, text, StringComparison.Ordinal);
        Assert.EndsWith("then try again.", text, StringComparison.Ordinal);
    }
}
