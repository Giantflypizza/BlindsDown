using BlindsDown.Core.Usage;
using BlindsDown.Usage;
using Microsoft.Win32;

// Deliberately not "BlindsDown.Tests.System": a namespace called System would shadow the real System
// namespace for every test file under BlindsDown.Tests.
namespace BlindsDown.Tests.SystemServices;

internal static class SystemTestSupport
{
    /// <summary>Tests that read machine-specific state (services, other processes) only run when asked.</summary>
    public static void SkipUnlessDeviceTests() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Set BLINDSDOWN_DEVICE_TESTS=1 to run tests that depend on this machine's processes and services.");

    public static string ProcessPath => Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
}

/// <summary>
/// A throwaway key under HKCU\Software\BlindsDownTests\{guid}, deleted on dispose. The only place tests
/// ever write to the registry.
/// </summary>
internal sealed class TestRegistryKey : IDisposable
{
    private const string ParentPath = @"Software\BlindsDownTests";

    public TestRegistryKey()
    {
        Path = ParentPath + @"\" + Guid.NewGuid().ToString("N");
        Key = Registry.CurrentUser.CreateSubKey(Path, writable: true);
    }

    /// <summary>Path relative to HKCU.</summary>
    public string Path { get; }

    public RegistryKey Key { get; }

    public void Dispose()
    {
        Key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        using RegistryKey? parent = Registry.CurrentUser.OpenSubKey(ParentPath, writable: true);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // A test running in parallel created its own key in between; the last one out removes the parent.
            }
        }
    }
}

internal sealed class FakeIdentityResolver : IProcessIdentityResolver
{
    public HashSet<string> RunningPackages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AppIdentity Resolve(int processId) => AppIdentityRules.Unknown(processId);

    public bool IsExeRunning(string exePath) => false;

    public bool IsPackageRunning(string packageFamilyName) => RunningPackages.Contains(packageFamilyName);
}

internal sealed class FakeConsentStoreSource : IConsentStoreSource
{
    private Action? _onChanged;
    private int _ensureWatchingCalls;

    public List<ConsentStoreEntry> Entries { get; } = [];

    public bool Throw { get; set; }

    /// <summary>Incremented from the monitor's poll thread too.</summary>
    public int EnsureWatchingCalls => Volatile.Read(ref _ensureWatchingCalls);

    public bool Disposed { get; private set; }

    public IReadOnlyList<ConsentStoreEntry> Read(CaptureKind kind) =>
        Throw ? throw new IOException("Simulated registry failure") : Entries.Where(e => e.Kind == kind).ToList();

    public void EnsureWatching(Action onChanged)
    {
        Interlocked.Increment(ref _ensureWatchingCalls);
        _onChanged = onChanged;
    }

    public void SimulateChange() => _onChanged?.Invoke();

    public void Dispose() => Disposed = true;
}
