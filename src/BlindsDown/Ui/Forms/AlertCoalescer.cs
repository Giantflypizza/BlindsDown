using BlindsDown.Core.Time;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// Decides whether a popup repeats one shown moments ago. Device notifications often arrive in bursts
/// (one per endpoint, or the same change reported by two sources), and a stack of identical popups is
/// noise. The window slides: a repeat keeps the entry alive, so a steady burst produces one popup.
/// </summary>
internal sealed class AlertCoalescer(IClock clock, TimeSpan window)
{
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>Identity of a popup for coalescing: same title and same message.</summary>
    public static string KeyOf(string title, string message) => title + "\u001F" + message;

    /// <summary>Records the alert and returns true when an identical one was seen within the window.</summary>
    public bool IsRepeat(string title, string message)
    {
        DateTimeOffset now = clock.UtcNow;
        foreach (string stale in _lastSeen.Where(kv => now - kv.Value >= window).Select(kv => kv.Key).ToList())
        {
            _lastSeen.Remove(stale);
        }

        string key = KeyOf(title, message);
        bool repeat = _lastSeen.ContainsKey(key);
        _lastSeen[key] = now;
        return repeat;
    }
}
