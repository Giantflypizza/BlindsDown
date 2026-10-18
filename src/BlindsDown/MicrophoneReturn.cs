using BlindsDown.Core.Devices;

namespace BlindsDown;

/// <summary>
/// When the helper turns microphone hardware back on, Windows takes a moment (often 0.5-3 s) to list its microphones
/// again. Restoring them before that would find them "not connected" and leave them muted (or turned off) until
/// BlindsDown runs again. Both the tray app and --restore wait with this check first. Engine thread only.
/// </summary>
internal static class MicrophoneReturn
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>True while a microphone of the given hardware is still missing.</summary>
    public static bool AnyMissing(IMicBackend backend, IReadOnlySet<string> hardwareIds) =>
        hardwareIds.Count > 0
        && backend.EnumerateMics().Any(m => m.HardwareInstanceId is string hardware && hardwareIds.Contains(hardware) && m.State == EndpointState.NotPresent);

    /// <summary>Waits (blocking, on the engine thread) until the microphones are back or <see cref="Patience"/> ran out.
    /// Never throws: a failed check counts as "not back yet".</summary>
    public static void WaitFor(IMicBackend backend, IReadOnlySet<string> hardwareIds)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!AnyMissing(backend, hardwareIds))
                {
                    return;
                }
            }
            catch (DeviceOperationException)
            {
                // Windows is still rebuilding its list: try again.
            }

            Thread.Sleep(250);
        }
    }
}
