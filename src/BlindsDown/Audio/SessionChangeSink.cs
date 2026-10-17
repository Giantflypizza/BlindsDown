using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// Receives "session created" (per endpoint) and "session state changed / disconnected" (per session)
/// notifications. One instance is registered everywhere: the monitor re-reads all sessions on any change, so
/// it doesn't need to know which session fired. Bursts (a call starting fires several events at once) are
/// coalesced into a single refresh on the engine thread.
/// </summary>
internal sealed class SessionChangeSink : IAudioSessionNotification, IAudioSessionEvents
{
    private readonly EngineThread _engine;
    private readonly Action _onEngineThread;
    private int _queued;

    public SessionChangeSink(EngineThread engine, Action onEngineThread)
    {
        _engine = engine;
        _onEngineThread = onEngineThread;
    }

    /// <summary>The new session object is borrowed: it is neither kept nor released here.</summary>
    public int OnSessionCreated(nint newSession) => Signal();

    public int OnStateChanged(AudioSessionState newState) => Signal();

    public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => Signal();

    public int OnDisplayNameChanged(nint newDisplayName, nint eventContext) => CoreAudioErrors.SOk;

    public int OnIconPathChanged(nint newIconPath, nint eventContext) => CoreAudioErrors.SOk;

    public int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext) => CoreAudioErrors.SOk;

    public int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext) => CoreAudioErrors.SOk;

    public int OnGroupingParamChanged(nint newGroupingParam, nint eventContext) => CoreAudioErrors.SOk;

    private int Signal()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            EngineDispatch.PostFromCallback(_engine, () =>
            {
                Volatile.Write(ref _queued, 0);
                _onEngineThread();
            });
        }

        return CoreAudioErrors.SOk;
    }
}
