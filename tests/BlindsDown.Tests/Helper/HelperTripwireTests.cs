using BlindsDown.Helper;
using BlindsDown.Infrastructure;
using BlindsDown.Tests.NoNetwork;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// The runtime network tripwire covers the helper too, as README and SECURITY say: the service and the elevated setup
/// runs install it (log only: they have no tray icon for an alert). Checked on the compiled program, like the
/// no-network rules, because calling these entry points would start a service or a setup run, and installing the
/// tripwire in the test process would change state other tests rely on.
/// </summary>
public sealed class HelperTripwireTests
{
    private static readonly string Tripwire = typeof(NetworkTripwire).FullName!;

    [Theory]
    [InlineData(typeof(HelperService), "Run")]
    [InlineData(typeof(HelperSetup), "RunElevated")]
    [InlineData(typeof(HelperSetup), "RelaunchUninstallElevated")]
    public void HelperProcessesInstallTheNetworkTripwire(Type type, string method)
    {
        using var scanner = new AssemblyScanner(typeof(HelperService).Assembly.Location);
        List<CallSite> calls = [.. scanner.CallSites().Where(c => c.Caller.DeclaringType == type.FullName && c.Caller.Name == method)];

        Assert.NotEmpty(calls);
        Assert.Contains(calls, c => c.Callee.DeclaringType == Tripwire && c.Callee.Name == nameof(NetworkTripwire.InstallLogOnly));
    }

    [Fact]
    public void TheServiceInstallsItBeforeItStartsServing()
    {
        // Everything the service loads while it runs is then reported (Install also reports what loaded before).
        using var scanner = new AssemblyScanner(typeof(HelperService).Assembly.Location);
        List<CallSite> calls = [.. scanner.CallSites().Where(c => c.Caller.DeclaringType == typeof(HelperService).FullName && c.Caller.Name == "Run")];

        int install = calls.First(c => c.Callee.DeclaringType == Tripwire && c.Callee.Name == nameof(NetworkTripwire.InstallLogOnly)).ILOffset;
        int serve = calls.First(c => c.Callee.DeclaringType == "System.ServiceProcess.ServiceBase" && c.Callee.Name == "Run").ILOffset;
        Assert.True(install < serve, "The tripwire is installed only after the service starts serving.");
    }
}
