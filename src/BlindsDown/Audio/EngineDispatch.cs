using BlindsDown.Infrastructure;

namespace BlindsDown.Audio;

/// <summary>Moves work between Windows' notification threads, the caller's thread and the engine thread.</summary>
internal static class EngineDispatch
{
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Queues <paramref name="action"/> from a Windows audio callback. Callbacks run on Windows' own threads
    /// while the audio service waits for them, so they may only queue work and return.
    /// </summary>
    public static void PostFromCallback(EngineThread engine, Action action)
    {
        try
        {
            engine.Post(action);
        }
        catch (ObjectDisposedException)
        {
            // The app is shutting down; the notification no longer matters.
        }
    }

    /// <summary>
    /// Runs <paramref name="teardown"/> on the engine thread and waits for it. Core Audio objects (and the
    /// unregistration of callbacks) belong to that thread, while Dispose may be called from the UI thread.
    /// </summary>
    public static void RunTeardown(EngineThread engine, Action teardown, string component)
    {
        if (engine.IsCurrentThread)
        {
            teardown();
            return;
        }

        try
        {
            _ = engine.Invoke(
                () =>
                {
                    teardown();
                    return true;
                },
                TeardownTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warn($"{component}: the device thread did not respond, so Windows audio notifications were not unregistered.");
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // Dispose usually runs while the app exits; a failed cleanup must not turn that into a crash.
            Log.Error($"{component}: cleaning up the Windows audio notifications failed", ex);
        }
    }
}
