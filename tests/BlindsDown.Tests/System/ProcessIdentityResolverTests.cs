using System.Diagnostics;
using BlindsDown.Core.Usage;
using BlindsDown.Interop.SystemApis;
using BlindsDown.Usage;

namespace BlindsDown.Tests.SystemServices;

/// <summary>Read-only: these only inspect processes.</summary>
public sealed class ProcessIdentityResolverTests
{
    private readonly ProcessIdentityResolver _resolver = new(new RunningProcessTable());

    [Fact]
    public void CurrentProcessIsIdentifiedByItsExePath()
    {
        string exe = SystemTestSupport.ProcessPath;

        AppIdentity app = _resolver.Resolve(Environment.ProcessId);

        Assert.Equal(Path.GetFullPath(exe).ToLowerInvariant(), app.Key);
        Assert.Equal(exe, app.ExePath, StringComparer.OrdinalIgnoreCase);
        Assert.Null(app.PackageFamilyName);
        Assert.Equal(AppIdentityRules.ForExePath(exe).DisplayName, app.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
    }

    [Fact]
    public void RepeatedLookupsComeFromTheCache() =>
        Assert.Same(_resolver.Resolve(Environment.ProcessId), _resolver.Resolve(Environment.ProcessId));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void InvalidPidsAreUnknown(int pid) => Assert.Equal(AppIdentityRules.Unknown(pid), _resolver.Resolve(pid));

    [Fact]
    public void ProcessesThatDoNotExistAreUnknown()
    {
        // PIDs are multiples of 4 and never get this large.
        const int Pid = 0x7FFFFFF0;

        Assert.Equal(AppIdentityRules.Unknown(Pid), _resolver.Resolve(Pid));
    }

    [Fact]
    public void RunningExeIsDetectedCaseInsensitively()
    {
        string exe = SystemTestSupport.ProcessPath;

        Assert.True(_resolver.IsExeRunning(exe));
        Assert.True(_resolver.IsExeRunning(exe.ToUpperInvariant()));
        Assert.False(_resolver.IsExeRunning(Path.Combine(Path.GetDirectoryName(exe)!, "blindsdown-not-running.exe")));
        Assert.False(_resolver.IsExeRunning(string.Empty));
        Assert.False(_resolver.IsExeRunning("\0invalid"));
    }

    [Fact]
    public void UninstalledPackagesAreNotRunning()
    {
        Assert.False(_resolver.IsPackageRunning("Contoso.NotInstalled_0000000000000"));
        Assert.False(_resolver.IsPackageRunning(string.Empty));
    }

    [Fact]
    public void ProcessListIncludesThisProcessWithItsCreationTime()
    {
        List<ProcessEntry>? processes = SystemQueries.ListProcesses();

        Assert.NotNull(processes);
        ProcessEntry self = Assert.Single(processes, p => p.ProcessId == Environment.ProcessId);
        nint handle = SystemQueries.OpenProcessForQuery(Environment.ProcessId);
        try
        {
            Assert.Equal(SystemQueries.GetCreationTime(handle), self.CreateTime);
        }
        finally
        {
            SystemQueries.CloseProcess(handle);
        }

        Assert.Equal(Path.GetFileName(SystemTestSupport.ProcessPath), self.ImageName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceHostNamesListUpToTwoServices()
    {
        Assert.Equal("Windows Audio (svchost)", ProcessIdentityResolver.ServiceHostDisplayName(["Windows Audio"]));
        Assert.Equal("A, B (svchost)", ProcessIdentityResolver.ServiceHostDisplayName(["A", "B"]));
        Assert.Equal("A, B and 3 more (svchost)", ProcessIdentityResolver.ServiceHostDisplayName(["A", "B", "C", "D", "E"]));
    }

    [Fact]
    public void SystemServicesAreNamedAfterTheServicesTheyHost()
    {
        SystemTestSupport.SkipUnlessDeviceTests();
        (int pid, string firstService) = FindServiceHost();

        AppIdentity app = _resolver.Resolve(pid);

        Assert.StartsWith(firstService, app.DisplayName, StringComparison.Ordinal);
        Assert.EndsWith("(svchost)", app.DisplayName, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "svchost.exe").ToLowerInvariant(), app.Key);
        TestContext.Current.TestOutputHelper?.WriteLine($"PID {pid}: {app.DisplayName}");
    }

    [Fact]
    public void PseudoProcessesWithoutAnImageAreNamed()
    {
        SystemTestSupport.SkipUnlessDeviceTests();

        AppIdentity system = _resolver.Resolve(4);

        Assert.Equal("pid:4", system.Key);
        Assert.Equal("System", system.DisplayName);
    }

    [Fact]
    public void RunningStoreAppsAreIdentifiedByPackageFamily()
    {
        SystemTestSupport.SkipUnlessDeviceTests();
        RunningProcess? packaged = new RunningProcessTable().Current().Processes.FirstOrDefault(p => p.PackageFamilyName is not null);
        Assert.SkipWhen(packaged is null, "No Store app is running.");

        AppIdentity app = _resolver.Resolve(packaged.ProcessId);

        Assert.Equal(packaged.PackageFamilyName, app.Key);
        Assert.Equal(packaged.PackageFamilyName, app.PackageFamilyName);
        Assert.True(_resolver.IsPackageRunning(packaged.PackageFamilyName!));
        Assert.DoesNotContain("ms-resource", app.DisplayName, StringComparison.OrdinalIgnoreCase);
        TestContext.Current.TestOutputHelper?.WriteLine($"{app.Key} -> {app.DisplayName}");
    }

    /// <summary>A svchost.exe running as SYSTEM (so OpenProcess is refused) that hosts at least one service.
    /// Service display names are localized, so none is hard-coded.</summary>
    private static (int ProcessId, string FirstService) FindServiceHost()
    {
        Process[] hosts = Process.GetProcessesByName("svchost");
        try
        {
            foreach (Process process in hosts)
            {
                nint handle = SystemQueries.OpenProcessForQuery(process.Id);
                SystemQueries.CloseProcess(handle);
                IReadOnlyList<string> services = SystemQueries.GetServiceDisplayNames(process.Id);
                if (handle == 0 && services.Count > 0)
                {
                    return (process.Id, services[0]);
                }
            }
        }
        finally
        {
            foreach (Process process in hosts)
            {
                process.Dispose();
            }
        }

        Assert.Skip("No service host that this process can't open was found.");
        return default;
    }
}
