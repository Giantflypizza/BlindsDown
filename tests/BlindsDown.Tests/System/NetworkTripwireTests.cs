using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.SystemApis;
using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

public sealed class NetworkTripwireTests
{
    [Theory]
    [InlineData("System.Net.Http", true)]
    [InlineData("System.Net.Sockets", true)]
    [InlineData("System.Net.Primitives", true)]
    [InlineData("system.net.requests", true)]
    [InlineData("System.Private.CoreLib", false)]
    [InlineData("System.Private.Uri", false)]
    [InlineData("BlindsDown", false)]
    public void ManagedNetworkingIsRecognisedByName(string name, bool expected) =>
        Assert.Equal(expected, NetworkTripwire.IsManagedNetworking(name));

    [Fact]
    public void NoManagedNetworkingIsLoadedInThisProcess()
    {
        // Exercise every read-only code path of the system services first (process inspection, version
        // resources, XML manifests and package resources, the usage registry, services, kernel objects), so
        // anything they pulled in would show up here whatever order the other tests run in.
        var resolver = new ProcessIdentityResolver();
        _ = resolver.Resolve(Environment.ProcessId);
        _ = resolver.Resolve(4);
        _ = resolver.IsPackageRunning("Contoso.NotInstalled_abc");
        _ = AppIdentityRules.ForPackage("Contoso.NotInstalled_abc");
        using (var manifest = new MemoryStream("<Package><Properties><DisplayName>x</DisplayName></Properties></Package>"u8.ToArray()))
        {
            _ = AppIdentityRules.ReadManifestDisplayName(manifest);
        }

        using (var monitor = new ConsentStoreMonitor(resolver))
        {
            _ = monitor.GetActiveSignals(CaptureKind.Microphone);
            _ = monitor.GetLastUsedRecords(DateTimeOffset.UtcNow.AddDays(-30));
        }

        _ = SystemQueries.GetServiceDisplayNames(Environment.ProcessId);
        _ = NetworkTripwire.LoadedNativeNetworkingModules();
        _ = SafeShell.IsAllowed(SafeShell.SoundUri);
        using (SingleInstance.TryAcquire("BlindsDownTests-" + Guid.NewGuid().ToString("N")))
        {
        }

        _ = SystemInfo.BootTimeUtc();
        _ = SystemInfo.IsRemoteSession();

        Assert.Empty(NetworkTripwire.LoadedManagedNetworkingAssemblies());
    }

    [Fact]
    public void InstallNeverThrowsAndReportsNothingHere()
    {
        var reported = new List<string>();

        NetworkTripwire.Install(name =>
        {
            lock (reported)
            {
                reported.Add(name);
            }
        });

        lock (reported)
        {
            Assert.Empty(reported);
        }
    }

    [Fact]
    public void NativeModuleListOnlyNamesNetworkingDlls()
    {
        string[] known = ["ws2_32", "mswsock", "winhttp", "wininet", "urlmon", "dnsapi", "iphlpapi", "webio", "nsi", "httpapi", "winnsi"];

        IReadOnlyList<string> modules = NetworkTripwire.LoadedNativeNetworkingModules();

        Assert.All(modules, m => Assert.Contains(Path.GetFileNameWithoutExtension(m), known, StringComparer.OrdinalIgnoreCase));
        TestContext.Current.TestOutputHelper?.WriteLine("Native networking modules (informational): " + string.Join(", ", modules));
    }
}
