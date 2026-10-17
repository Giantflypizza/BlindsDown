using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// Receives volume/mute notifications for ONE endpoint (the notification itself doesn't say which endpoint,
/// so each endpoint gets its own sink). Copies the data out of Windows' buffer and queues it to the engine
/// thread; nothing else happens on Windows' thread.
/// </summary>
internal sealed class MuteNotificationSink : IAudioEndpointVolumeCallback
{
    private readonly EngineThread _engine;
    private readonly string _endpointId;
    private readonly Action<string, VolumeNotification> _onEngineThread;

    public MuteNotificationSink(EngineThread engine, string endpointId, Action<string, VolumeNotification> onEngineThread)
    {
        _engine = engine;
        _endpointId = endpointId;
        _onEngineThread = onEngineThread;
    }

    public int OnNotify(nint notificationData)
    {
        if (notificationData != 0)
        {
            VolumeNotification notification = VolumeNotification.Read(notificationData);
            EngineDispatch.PostFromCallback(_engine, () => _onEngineThread(_endpointId, notification));
        }

        return CoreAudioErrors.SOk;
    }
}
