using System.Globalization;
using System.Text;

namespace BlindsDown.Core.Devices;

/// <summary>
/// Plain-text dump of every microphone's state ("BlindsDown.exe --snapshot file.txt"), used to compare
/// device state before and after a lock, by hand or in tests. Sorted and stable so two dumps can be diffed.
/// </summary>
public static class SnapshotText
{
    public static string Format(AudioSnapshot snapshot, bool includeTimestamp = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BlindsDown device snapshot");
        if (includeTimestamp)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Taken: {snapshot.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"));
        }

        sb.AppendLine();
        sb.AppendLine("Default microphones:");
        sb.AppendLine("  console:        " + NameOf(snapshot, snapshot.Defaults.Console));
        sb.AppendLine("  multimedia:     " + NameOf(snapshot, snapshot.Defaults.Multimedia));
        sb.AppendLine("  communications: " + NameOf(snapshot, snapshot.Defaults.Communications));
        sb.AppendLine();
        sb.AppendLine("Microphones (state | mute | hardware mute | virtual | name | id | hardware):");
        foreach (MicEndpoint mic in snapshot.Mics.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            string mute = mic.Muted switch
            {
                true => "muted",
                false => "unmuted",
                null => "n/a",
            };
            sb.Append("  ")
              .Append(mic.State.ToString().PadRight(10))
              .Append(" | ").Append(mute.PadRight(7))
              .Append(" | ").Append(mic.HasHardwareMute ? "hw-mute" : "sw-mute")
              .Append(" | ").Append(mic.IsVirtual ? "virtual " : "physical")
              .Append(" | ").Append(mic.Name)
              .Append(" | ").Append(mic.Id)
              .Append(" | ").AppendLine(mic.HardwareInstanceId ?? "?");
        }

        return sb.ToString();
    }

    private static string NameOf(AudioSnapshot snapshot, string? id) =>
        id is null ? "(none)" : $"{snapshot.Find(id)?.Name ?? "?"} [{id}]";
}
