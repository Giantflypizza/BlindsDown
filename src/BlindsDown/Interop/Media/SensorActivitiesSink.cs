using System.Runtime.InteropServices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Interop.Media;

/// <summary>
/// One process's use of one camera, copied out of a sensor activity report. <see cref="SymbolicLink"/> is the camera's
/// device interface path; <see cref="FriendlyName"/> is the name the report gives (null when it gives none).
/// </summary>
internal sealed record CameraActivityEntry(string SymbolicLink, string? FriendlyName, int ProcessId, bool Streaming);

/// <summary>
/// Receives Media Foundation's sensor activity reports. Media Foundation calls <see cref="OnActivitiesReport"/> on
/// its own worker threads and the report is only borrowed, so this copies everything into plain
/// <see cref="CameraActivityEntry"/> values right away and hands them on; the handler must only store them and queue
/// work. Nothing ever escapes into native code: an exception from reading or from the handler goes to
/// <paramref name="onFailure"/> and the call still returns S_OK.
/// </summary>
/// <param name="onReport">Gets the entries of a report, or null when the report itself couldn't be read.</param>
/// <param name="onFailure">Gets an exception that would otherwise have reached Media Foundation.</param>
internal sealed class SensorActivitiesSink(Action<IReadOnlyList<CameraActivityEntry>?> onReport, Action<Exception> onFailure)
    : IMFSensorActivitiesReportCallback
{
    public int OnActivitiesReport(nint activitiesReport)
    {
        try
        {
            onReport(Read(activitiesReport));
        }
        catch (Exception ex)
        {
            try
            {
                onFailure(ex);
            }
            catch (Exception)
            {
                // Even the failure handler failed: there is nobody left to tell, and Media Foundation must not see it.
            }
        }

        return CoreAudioErrors.SOk;
    }

    /// <summary>Copies a borrowed IMFSensorActivitiesReport. Returns null when it can't be read at all.</summary>
    internal static IReadOnlyList<CameraActivityEntry>? Read(nint activitiesReport)
    {
        if (activitiesReport == 0)
        {
            return null;
        }

        // The caller keeps its reference: add one for the wrapper to release.
        _ = Marshal.AddRef(activitiesReport);
        using ComRef<IMFSensorActivitiesReport>? report = ComRef.TryTake<IMFSensorActivitiesReport>(activitiesReport);
        return report is null ? null : SensorActivityReader.Read(report.Value);
    }
}

/// <summary>Reads sensor activity reports into <see cref="CameraActivityEntry"/> values. Never throws for a bad entry.</summary>
internal static unsafe class SensorActivityReader
{
    /// <summary>WCHARs for a name or symbolic link on the first try (device interface paths are a few hundred).</summary>
    internal const int TextCapacity = 1024;

    /// <summary>Upper bound for a retry with the size the report asked for.</summary>
    private const uint MaxTextCapacity = 32 * 1024;

    /// <summary>Sanity limits: a report never has anywhere near this many cameras or processes.</summary>
    private const uint MaxCameras = 256;

    private const uint MaxProcesses = 4096;

    /// <summary>
    /// All (camera, process) pairs of a report. Returns null only when the report's count can't be read; a camera or
    /// process entry that can't be read is skipped (the next report brings it back).
    /// </summary>
    public static IReadOnlyList<CameraActivityEntry>? Read(IMFSensorActivitiesReport report)
    {
        if (report.GetCount(out uint cameras) < 0)
        {
            return null;
        }

        var entries = new List<CameraActivityEntry>();
        for (uint i = 0; i < Math.Min(cameras, MaxCameras); i++)
        {
            if (report.GetActivityReport(i, out nint pointer) < 0)
            {
                continue;
            }

            using ComRef<IMFSensorActivityReport>? camera = ComRef.TryTake<IMFSensorActivityReport>(pointer);
            if (camera is not null)
            {
                ReadCamera(camera.Value, entries);
            }
        }

        return entries;
    }

    /// <summary>The text an IMFSensorActivityReport string getter copies, or null when it fails or is empty.</summary>
    internal static string? ReadText(IMFSensorActivityReport camera, bool symbolicLink)
    {
        char* buffer = stackalloc char[TextCapacity];
        buffer[0] = '\0';
        int hr = Get(camera, symbolicLink, buffer, TextCapacity, out uint written);
        if (hr >= 0)
        {
            return Terminated(buffer, TextCapacity);
        }

        // Too small for this one: try once more with the size it reported (in WCHARs, maybe without the terminator).
        if (written < TextCapacity || written >= MaxTextCapacity)
        {
            return null;
        }

        int capacity = (int)written + 1;
        char[] larger = new char[capacity];
        fixed (char* retry = larger)
        {
            return Get(camera, symbolicLink, retry, (uint)capacity, out _) >= 0 ? Terminated(retry, capacity) : null;
        }
    }

    private static void ReadCamera(IMFSensorActivityReport camera, List<CameraActivityEntry> entries)
    {
        string? link = ReadText(camera, symbolicLink: true);
        if (link is null || camera.GetProcessCount(out uint processes) < 0)
        {
            return;
        }

        string? name = ReadText(camera, symbolicLink: false);
        for (uint p = 0; p < Math.Min(processes, MaxProcesses); p++)
        {
            if (camera.GetProcessActivity(p, out nint pointer) < 0)
            {
                continue;
            }

            using ComRef<IMFSensorProcessActivity>? process = ComRef.TryTake<IMFSensorProcessActivity>(pointer);
            if (process is not null
                && process.Value.GetProcessId(out uint processId) >= 0
                && process.Value.GetStreamingState(out int streaming) >= 0)
            {
                // A PID above int.MaxValue doesn't exist; it becomes negative and is dropped as invalid later.
                entries.Add(new CameraActivityEntry(link, name, unchecked((int)processId), streaming != 0));
            }
        }
    }

    private static int Get(IMFSensorActivityReport camera, bool symbolicLink, char* buffer, uint capacity, out uint written) =>
        symbolicLink
            ? camera.GetSymbolicLink((nint)buffer, capacity, out written)
            : camera.GetFriendlyName((nint)buffer, capacity, out written);

    /// <summary>The text up to the first null terminator. A buffer without one is not trusted (null).</summary>
    private static string? Terminated(char* buffer, int capacity)
    {
        var text = new ReadOnlySpan<char>(buffer, capacity);
        int end = text.IndexOf('\0');
        if (end < 0)
        {
            return null;
        }

        string value = text[..end].ToString().Trim();
        return value.Length == 0 ? null : value;
    }
}
