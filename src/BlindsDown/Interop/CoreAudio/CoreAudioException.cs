namespace BlindsDown.Interop.CoreAudio;

/// <summary>
/// A failed Core Audio call, carrying its HRESULT. Used inside the audio layer only: at the
/// <see cref="Core.Devices.IMicBackend"/> boundary it becomes a <see cref="Core.Devices.DeviceOperationException"/>,
/// and the watchers log it and carry on.
/// </summary>
internal sealed class CoreAudioException : Exception
{
    private readonly string? _explanation;

    public CoreAudioException()
    {
    }

    public CoreAudioException(string message)
        : base(message)
    {
    }

    public CoreAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CoreAudioException(string operation, int hresult)
        : base($"{operation} failed: {CoreAudioErrors.Describe(hresult)}")
    {
        HResult = hresult;
        _explanation = CoreAudioErrors.Describe(hresult);
    }

    /// <summary>The reason without the operation name, for user-facing messages ("Could not mute X: reason").</summary>
    public string Explanation => _explanation ?? Message;
}
