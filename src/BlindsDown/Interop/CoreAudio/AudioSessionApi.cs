using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

// Audio sessions (audiopolicy.h): which processes have streams open on an endpoint. BlindsDown only reads
// sessions; it never opens a stream (there is deliberately no IAudioClient declaration anywhere).

/// <summary>
/// IAudioSessionManager2, including the two IAudioSessionManager methods it inherits (slots 3 and 4). Those
/// two would create a new session on the endpoint when given an unknown GUID, so they are declared
/// obsolete-as-error and cannot be called.
/// </summary>
[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);

    /// <summary>
    /// Returns an IAudioSessionEnumerator. Windows only starts delivering session-created notifications to a
    /// manager after its session list has been read once (this plus IAudioSessionEnumerator::GetCount).
    /// </summary>
    [PreserveSig]
    int GetSessionEnumerator(out nint sessionEnum);

    /// <summary>Must be called from an MTA thread, otherwise notifications never arrive.</summary>
    [PreserveSig]
    int RegisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    /// <summary>Returns an IAudioSessionControl (query it for IAudioSessionControl2).</summary>
    [PreserveSig]
    int GetSession(int sessionIndex, out nint session);
}

/// <summary>
/// IAudioSessionControl2, including the nine IAudioSessionControl methods it inherits (slots 3 to 11).
/// The setters are declared obsolete-as-error: BlindsDown never changes another app's session.
/// </summary>
[ComImport]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    /// <summary>Returns a CoTaskMemAlloc'ed string that the caller frees.</summary>
    [PreserveSig]
    int GetDisplayName(out nint displayName);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

    /// <summary>Returns a CoTaskMemAlloc'ed string that the caller frees.</summary>
    [PreserveSig]
    int GetIconPath(out nint iconPath);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IAudioSessionEvents notifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IAudioSessionEvents notifications);

    /// <summary>Returns a CoTaskMemAlloc'ed string that the caller frees. Shared by all sessions of one app.</summary>
    [PreserveSig]
    int GetSessionIdentifier(out nint sessionId);

    /// <summary>Returns a CoTaskMemAlloc'ed string that the caller frees. Unique per session.</summary>
    [PreserveSig]
    int GetSessionInstanceIdentifier(out nint instanceId);

    /// <summary>May return <see cref="CoreAudioErrors.NoSingleProcess"/>, which is a success code.</summary>
    [PreserveSig]
    int GetProcessId(out uint processId);

    /// <summary>S_OK for the system sounds session, S_FALSE for every other session.</summary>
    [PreserveSig]
    int IsSystemSoundsSession();

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetDuckingPreference(int optOut);
}

/// <summary>
/// "A session was created" notifications, implemented by BlindsDown. The session pointer is borrowed: it is
/// neither kept nor released.
/// </summary>
[ComImport]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(nint newSession);
}

/// <summary>Per-session events, implemented by BlindsDown. All pointer arguments are borrowed.</summary>
[ComImport]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged(nint newDisplayName, nint eventContext);

    [PreserveSig]
    int OnIconPathChanged(nint newIconPath, nint eventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext);

    [PreserveSig]
    int OnGroupingParamChanged(nint newGroupingParam, nint eventContext);

    [PreserveSig]
    int OnStateChanged(AudioSessionState newState);

    [PreserveSig]
    int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason);
}
