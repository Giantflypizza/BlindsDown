using BlindsDown.Infrastructure;

namespace BlindsDown.Audio;

/// <summary>
/// Logs each ongoing problem once instead of on every poll. The audio layer re-checks devices every few
/// seconds; while the audio service is down, logging every failed check would push everything useful out of
/// the size-limited log. Not thread-safe: used on the engine thread only.
/// </summary>
internal sealed class ProblemLog(string component)
{
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);

    /// <summary>Logs <paramref name="message"/> unless the same problem was already logged under <paramref name="topic"/>.</summary>
    public void Report(string topic, string message)
    {
        if (_active.TryGetValue(topic, out string? previous) && previous == message)
        {
            return;
        }

        _active[topic] = message;
        Log.Warn($"{component}: {message}");
    }

    /// <summary>Marks <paramref name="topic"/> as working again (and logs the recovery if it had failed).</summary>
    public void Resolve(string topic)
    {
        if (_active.Remove(topic))
        {
            Log.Info($"{component}: working again: {topic}");
        }
    }
}
