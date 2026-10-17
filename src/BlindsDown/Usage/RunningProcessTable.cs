using BlindsDown.Infrastructure;
using BlindsDown.Interop.SystemApis;

namespace BlindsDown.Usage;

/// <summary>One running process. <see cref="ExePath"/> is null when it can't be determined;
/// <see cref="PackageFamilyName"/> is null for classic desktop apps.</summary>
internal sealed record RunningProcess(int ProcessId, string? ExePath, string? PackageFamilyName);

/// <summary>An immutable view of the running processes at one moment.</summary>
internal sealed class RunningProcessSnapshot
{
    public RunningProcessSnapshot(IReadOnlyList<RunningProcess> processes)
    {
        Processes = processes;
        ExePaths = processes.Select(p => p.ExePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        PackageFamilyNames = processes.Select(p => p.PackageFamilyName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static RunningProcessSnapshot Empty { get; } = new([]);

    public IReadOnlyList<RunningProcess> Processes { get; }

    /// <summary>Full exe paths, compared case-insensitively like the file system.</summary>
    public IReadOnlySet<string> ExePaths { get; }

    public IReadOnlySet<string> PackageFamilyNames { get; }
}

/// <summary>
/// Which exe paths and Store packages are running right now, refreshed at most every 2 s (the usage registry
/// is checked against it on every change and poll). Facts about each process are kept per PID + creation
/// time, so a refresh only inspects processes that started since the previous one, and a reused PID is
/// always inspected afresh. Thread-safe.
/// </summary>
internal sealed class RunningProcessTable
{
    private const long MaxAgeMs = 2000;
    private readonly object _gate = new();
    private Dictionary<(int ProcessId, long CreateTime), RunningProcess> _known = [];
    private RunningProcessSnapshot _snapshot = RunningProcessSnapshot.Empty;
    private long _takenAt;
    private bool _hasSnapshot;
    private bool _listFailureLogged;

    public static RunningProcessTable Shared { get; } = new();

    public RunningProcessSnapshot Current()
    {
        lock (_gate)
        {
            if (!_hasSnapshot || Environment.TickCount64 - _takenAt >= MaxAgeMs)
            {
                Refresh();
                _takenAt = Environment.TickCount64;
                _hasSnapshot = true;
            }

            return _snapshot;
        }
    }

    private static RunningProcess Inspect(int processId)
    {
        nint process = SystemQueries.OpenProcessForQuery(processId);
        if (process != 0)
        {
            try
            {
                return new RunningProcess(processId, SystemQueries.GetImagePath(process), SystemQueries.GetPackageFamilyName(process));
            }
            finally
            {
                SystemQueries.CloseProcess(process);
            }
        }

        // SYSTEM services and other users' processes: the kernel still names the image. Such processes
        // never carry a Store package of this user.
        string? ntPath = SystemQueries.GetImageNtPath(processId);
        return new RunningProcess(processId, ntPath is null ? null : ImagePaths.ToDosPath(ntPath), null);
    }

    private void Refresh()
    {
        List<ProcessEntry>? entries = SystemQueries.ListProcesses();
        if (entries is null)
        {
            // Keep the previous snapshot: slightly stale beats "nothing is running".
            if (!_listFailureLogged)
            {
                _listFailureLogged = true;
                Log.Warn("Could not list running processes; usage-registry checks use the previous list.");
            }

            return;
        }

        var known = new Dictionary<(int ProcessId, long CreateTime), RunningProcess>(entries.Count);
        var processes = new List<RunningProcess>(entries.Count);
        foreach (ProcessEntry entry in entries)
        {
            if (entry.ProcessId <= 0)
            {
                continue;
            }

            (int, long) key = (entry.ProcessId, entry.CreateTime);
            if (!_known.TryGetValue(key, out RunningProcess? process))
            {
                process = Inspect(entry.ProcessId);
            }

            known[key] = process;
            processes.Add(process);
        }

        _known = known;
        _snapshot = new RunningProcessSnapshot(processes);
    }
}
