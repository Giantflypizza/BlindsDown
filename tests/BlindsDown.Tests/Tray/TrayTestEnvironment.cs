using System.Runtime.ExceptionServices;

namespace BlindsDown.Tests.Tray;

/// <summary>Helpers for tests that touch real Windows UI (opt-in) and need an STA UI thread.</summary>
internal static class TrayTestEnvironment
{
    /// <summary>Skips unless BLINDSDOWN_DEVICE_TESTS=1 (real hotkeys, windows, audio output).</summary>
    public static void RequireRealUi() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Touches real Windows UI; set BLINDSDOWN_DEVICE_TESTS=1 to run.");

    /// <summary>Runs <paramref name="action"/> on a fresh STA thread, like the app's UI thread, and rethrows its failure.</summary>
    public static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
