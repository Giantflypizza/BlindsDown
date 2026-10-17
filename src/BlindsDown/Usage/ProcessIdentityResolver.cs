using System.Globalization;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.SystemApis;

namespace BlindsDown.Usage;

/// <summary>
/// See <see cref="IProcessIdentityResolver"/>. Thread-safe.
/// Identities are cached for 5 minutes by PID + process creation time: Windows reuses PIDs within seconds,
/// and the creation time is what tells a new process apart from an old one with the same PID.
/// </summary>
internal sealed class ProcessIdentityResolver : IProcessIdentityResolver
{
    private const int MaxCachedProcesses = 512;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly ExpiringCache<(int ProcessId, long CreationTime), AppIdentity> _cache = new(CacheLifetime, MaxCachedProcesses);
    private readonly RunningProcessTable _running;

    public ProcessIdentityResolver()
        : this(RunningProcessTable.Shared)
    {
    }

    internal ProcessIdentityResolver(RunningProcessTable running)
    {
        _running = running;
    }

    public AppIdentity Resolve(int processId)
    {
        if (processId <= 0)
        {
            return AppIdentityRules.Unknown(processId);
        }

        try
        {
            return ResolveCore(processId);
        }
        catch (Exception ex)
        {
            // Naming is cosmetic: never let it break an alert or the usage tracker.
            Log.Warn(string.Create(CultureInfo.InvariantCulture, $"Could not identify process {processId}: {ex.GetType().Name}: {ex.Message}"));
            return AppIdentityRules.Unknown(processId);
        }
    }

    public bool IsExeRunning(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            return _running.Current().ExePaths.Contains(Path.GetFullPath(exePath));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    public bool IsPackageRunning(string packageFamilyName) =>
        !string.IsNullOrWhiteSpace(packageFamilyName) && _running.Current().PackageFamilyNames.Contains(packageFamilyName);

    /// <summary>"Windows Audio (svchost)"; with many services, the first two and a count.</summary>
    internal static string ServiceHostDisplayName(IReadOnlyList<string> serviceDisplayNames)
    {
        const int Shown = 2;
        string names = string.Join(", ", serviceDisplayNames.Take(Shown));
        if (serviceDisplayNames.Count > Shown)
        {
            names += string.Create(CultureInfo.InvariantCulture, $" and {serviceDisplayNames.Count - Shown} more");
        }

        return names + " (svchost)";
    }

    private static AppIdentity FromOpenProcess(int processId, nint process)
    {
        string? path = SystemQueries.GetImagePath(process);
        string? family = SystemQueries.GetPackageFamilyName(process);
        if (family is not null)
        {
            return AppIdentityRules.ForPackage(family, SystemQueries.GetPackageFullName(process), path);
        }

        path ??= KernelImagePath(processId);
        return path is null ? AppIdentityRules.Unknown(processId) : FromExePath(processId, path);
    }

    private static AppIdentity FromExePath(int processId, string exePath)
    {
        AppIdentity identity = AppIdentityRules.ForExePath(exePath);
        if (!Path.GetFileName(exePath).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return identity;
        }

        // One svchost.exe hosts one or more Windows services, and their names say far more than
        // "Host Process for Windows Services". Matching by PID means an impostor svchost.exe gets none.
        IReadOnlyList<string> services = SystemQueries.GetServiceDisplayNames(processId);
        return services.Count == 0 ? identity : identity with { DisplayName = ServiceHostDisplayName(services) };
    }

    private static string? KernelImagePath(int processId) =>
        SystemQueries.GetImageNtPath(processId) is { } ntPath ? ImagePaths.ToDosPath(ntPath) : null;

    private AppIdentity ResolveCore(int processId)
    {
        nint process = SystemQueries.OpenProcessForQuery(processId);
        if (process == 0)
        {
            return ResolveWithoutHandle(processId);
        }

        try
        {
            long? created = SystemQueries.GetCreationTime(process);
            if (created is null)
            {
                return FromOpenProcess(processId, process);
            }

            (int, long) key = (processId, created.Value);
            if (_cache.TryGet(key, out AppIdentity? cached))
            {
                return cached;
            }

            AppIdentity identity = FromOpenProcess(processId, process);
            _cache.Set(key, identity);
            return identity;
        }
        finally
        {
            SystemQueries.CloseProcess(process);
        }
    }

    /// <summary>
    /// OpenProcess is refused for SYSTEM services and other users' processes. The kernel's process list
    /// still gives their creation time (for a reuse-safe cache key) and image name, and
    /// SystemProcessIdInformation gives the full image path.
    /// </summary>
    private AppIdentity ResolveWithoutHandle(int processId)
    {
        List<ProcessEntry>? processes = SystemQueries.ListProcesses();
        int index = processes?.FindIndex(p => p.ProcessId == processId) ?? -1;
        if (processes is null || index < 0)
        {
            // Already gone (or the list failed): nothing reliable to cache.
            return AppIdentityRules.Unknown(processId);
        }

        ProcessEntry entry = processes[index];
        (int, long) key = (processId, entry.CreateTime);
        if (_cache.TryGet(key, out AppIdentity? cached))
        {
            return cached;
        }

        AppIdentity identity;
        if (KernelImagePath(processId) is { } path)
        {
            identity = FromExePath(processId, path);
        }
        else if (entry.ImageName.Length > 0)
        {
            // Pseudo-processes without an image file: "System", "Registry", "Memory Compression".
            identity = AppIdentityRules.Unknown(processId) with { DisplayName = entry.ImageName };
        }
        else
        {
            identity = AppIdentityRules.Unknown(processId);
        }

        _cache.Set(key, identity);
        return identity;
    }
}
