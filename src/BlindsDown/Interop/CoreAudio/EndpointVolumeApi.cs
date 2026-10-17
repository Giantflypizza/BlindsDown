using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

// Endpoint volume (endpointvolume.h). BlindsDown reads and changes only the MUTE state: every volume setter
// is declared obsolete-as-error, so changing a volume level cannot even be compiled.

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    /// <summary>
    /// <paramref name="mute"/> is a Win32 BOOL. The event context is echoed back to every
    /// <see cref="IAudioEndpointVolumeCallback"/>, which is how BlindsDown recognises its own changes.
    /// </summary>
    [PreserveSig]
    int SetMute(int mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute(out int mute);

    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    /// <summary>ENDPOINT_HARDWARE_SUPPORT_* bits; see <see cref="CoreAudioConstants.HardwareSupportMute"/>.</summary>
    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

/// <summary>
/// Volume/mute change notifications for one endpoint, implemented by BlindsDown. Called on a Windows thread;
/// the data pointer is valid only during the call.
/// </summary>
[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    /// <summary><paramref name="notificationData"/> points to an AUDIO_VOLUME_NOTIFICATION_DATA.</summary>
    [PreserveSig]
    int OnNotify(nint notificationData);
}

/// <summary>
/// The part of AUDIO_VOLUME_NOTIFICATION_DATA BlindsDown reads: GUID guidEventContext at offset 0 and
/// BOOL bMuted at offset 16 (then float fMasterVolume, UINT nChannels and a variable-length channel array).
/// </summary>
internal readonly record struct VolumeNotification(Guid EventContext, bool Muted)
{
    public const int EventContextOffset = 0;
    public const int MutedOffset = 16;

    /// <summary>Copies the fields out of the buffer Windows passes to OnNotify.</summary>
    public static unsafe VolumeNotification Read(nint notificationData)
    {
        byte* data = (byte*)notificationData;
        return new VolumeNotification(*(Guid*)(data + EventContextOffset), *(int*)(data + MutedOffset) != 0);
    }
}
