namespace BlindsDown.Core.Usage;

/// <summary>
/// Helpers for Windows' per-app usage registry (the data behind Settings > Privacy > Microphone):
/// HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{microphone|webcam}.
/// Store apps have a subkey named after their package family; desktop apps live under "NonPackaged"
/// with the exe path as key name ('\' replaced by '#'). LastUsedTimeStart/Stop are UTC FILETIMEs and
/// Stop is 0 while the app is using the device.
///
/// This is only a fallback: newer Windows builds are reported to stop updating it, and entries can be
/// stale after a crash. BlindsDown's main sources are live audio sessions and the camera activity monitor.
/// </summary>
public static class ConsentStore
{
    public const string MicrophoneKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    public const string WebcamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    public const string NonPackagedSubkey = "NonPackaged";

    /// <summary>The key name Windows uses for a desktop app. ('#' is a legal path character, so decoding
    /// key names back to paths is ambiguous; always encode the running process's path and compare.)</summary>
    public static string EncodeNonPackagedKeyName(string exePath) => exePath.Replace('\\', '#');

    /// <summary>Best-effort decode for display only.</summary>
    public static string DecodeNonPackagedKeyNameForDisplay(string keyName) => keyName.Replace('#', '\\');

    public static DateTimeOffset? FromFileTime(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// "In use" according to the registry: started, not stopped, started after the last boot (older
    /// entries with Stop == 0 are leftovers from a crash or power cut) and the app is actually running.
    /// </summary>
    public static bool LooksInUse(long lastUsedStart, long lastUsedStop, DateTimeOffset bootTime, bool processRunning)
    {
        DateTimeOffset? start = FromFileTime(lastUsedStart);
        return start is not null && lastUsedStop == 0 && start.Value >= bootTime && processRunning;
    }
}
