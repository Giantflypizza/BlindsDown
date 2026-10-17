using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace BlindsDown.Infrastructure;

/// <summary>
/// Runtime check behind the "never connects to the internet" promise: reports if any
/// System.Net.* assembly is ever loaded into the process, and lists networking DLLs present in
/// the process (informational only: Windows itself may load some into any process).
/// Nothing here throws: a broken tripwire must never take the app down.
/// </summary>
internal static class NetworkTripwire
{
    private const string ManagedNetworkingPrefix = "System.Net";

    /// <summary>Winsock, WinHTTP, WinINet, URL Moniker, DNS, IP helper and HTTP server DLLs (names without ".dll").</summary>
    private static readonly HashSet<string> NativeNetworkingModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ws2_32",
        "mswsock",
        "winhttp",
        "wininet",
        "urlmon",
        "dnsapi",
        "iphlpapi",
        "webio",
        "nsi",
        "httpapi",
        "winnsi",
    };

    private static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.OrdinalIgnoreCase);
    private static Action<string>? _onManagedNetworkingLoaded;
    private static int _installed;

    /// <summary>Starts watching. <paramref name="onManagedNetworkingLoaded"/> receives the assembly name
    /// (it may be called on any thread). Each assembly is reported once; later calls to Install are ignored.</summary>
    public static void Install(Action<string> onManagedNetworkingLoaded)
    {
        if (onManagedNetworkingLoaded is null || Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        try
        {
            _onManagedNetworkingLoaded = onManagedNetworkingLoaded;

            // Subscribe before scanning, so an assembly loading in between is not missed.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (string name in LoadedManagedNetworkingAssemblies())
            {
                Report(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not install the network tripwire", ex);
        }
    }

    /// <summary>Managed networking assemblies currently loaded (should always be empty).</summary>
    public static IReadOnlyList<string> LoadedManagedNetworkingAssemblies()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .OfType<string>()
                .Where(IsManagedNetworking)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Could not list loaded assemblies", ex);
            return [];
        }
    }

    /// <summary>Networking-related native DLLs currently loaded into the process, e.g. ws2_32.dll.</summary>
    public static IReadOnlyList<string> LoadedNativeNetworkingModules()
    {
        var found = new List<string>();
        try
        {
            using Process self = Process.GetCurrentProcess();
            foreach (ProcessModule module in self.Modules)
            {
                using (module)
                {
                    string name = module.ModuleName;
                    if (NativeNetworkingModules.Contains(Path.GetFileNameWithoutExtension(name)))
                    {
                        found.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Typically a Win32Exception (ERROR_PARTIAL_COPY) when a DLL loads or unloads mid-enumeration.
            // This list feeds a diagnostics page only, so any failure is reported as "nothing found".
            Log.Warn($"Could not list loaded modules: {ex.GetType().Name}: {ex.Message}");
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool IsManagedNetworking(string assemblyName) =>
        assemblyName.StartsWith(ManagedNetworkingPrefix, StringComparison.OrdinalIgnoreCase);

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        // Runs inside the assembly load of whatever code triggered it: an exception here would surface there.
        try
        {
            string? name = args.LoadedAssembly.GetName().Name;
            if (name is not null && IsManagedNetworking(name))
            {
                Report(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Network tripwire check failed", ex);
        }
    }

    private static void Report(string assemblyName)
    {
        if (!Reported.TryAdd(assemblyName, true))
        {
            return;
        }

        Log.Error($"Network tripwire: managed networking assembly loaded: {assemblyName}");
        try
        {
            _onManagedNetworkingLoaded?.Invoke(assemblyName);
        }
        catch (Exception ex)
        {
            Log.Error("Network tripwire handler failed", ex);
        }
    }
}
