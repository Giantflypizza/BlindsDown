using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>The endpoint properties BlindsDown shows and keys exclusions on. Each is null when Windows has no value.</summary>
internal sealed record EndpointProperties(string? FriendlyName, string? Description, string? HardwareInstanceId, string? AdapterName)
{
    public static EndpointProperties Empty { get; } = new(null, null, null, null);

    /// <summary>A name that is never empty, even for an endpoint whose properties can't be read.</summary>
    public string DisplayName => FriendlyName ?? Description ?? "Microphone";
}

/// <summary>Read-only helpers for one <see cref="IMMDevice"/>. None of them throws for an unusable device.</summary>
internal static class EndpointReader
{
    /// <summary>The endpoint ID, or null when the device object no longer works.</summary>
    public static string? GetId(IMMDevice device) =>
        device.GetId(out nint text) >= 0 ? CoTaskMemText.Take(text) : null;

    public static EndpointState? GetState(IMMDevice device) =>
        device.GetState(out EndpointState state) >= 0 ? state : null;

    /// <summary>Render or capture, via IMMEndpoint (endpoint ID formats are not documented, so never parse them).</summary>
    public static DataFlow? GetDataFlow(IMMDevice device) =>
        device is IMMEndpoint endpoint && endpoint.GetDataFlow(out DataFlow flow) >= 0 ? flow : null;

    /// <summary>Reads the names and the hardware ID. Works in every endpoint state, including NOTPRESENT.</summary>
    public static EndpointProperties ReadProperties(IMMDevice device)
    {
        if (device.OpenPropertyStore(CoreAudioConstants.StgmRead, out nint pointer) < 0)
        {
            return EndpointProperties.Empty;
        }

        using ComRef<IPropertyStore>? store = ComRef.TryTake<IPropertyStore>(pointer);
        if (store is null)
        {
            return EndpointProperties.Empty;
        }

        return new EndpointProperties(
            PropertyStoreReader.ReadString(store.Value, EndpointPropertyKeys.FriendlyName),
            PropertyStoreReader.ReadString(store.Value, EndpointPropertyKeys.DeviceDescription),
            ParseControllerDeviceId(PropertyStoreReader.ReadString(store.Value, EndpointPropertyKeys.ControllerDeviceId)),
            PropertyStoreReader.ReadString(store.Value, EndpointPropertyKeys.AdapterName));
    }

    /// <summary>
    /// Activates a Core Audio interface on the endpoint (e.g. IAudioEndpointVolume). Returns null on failure,
    /// with the reason in <paramref name="hresult"/>.
    /// </summary>
    public static ComRef<T>? TryActivate<T>(IMMDevice device, out int hresult)
        where T : class
    {
        Guid iid = typeof(T).GUID;
        hresult = device.Activate(ref iid, CoreAudioConstants.ClsCtxInprocServer, 0, out nint pointer);
        if (hresult < 0)
        {
            return null;
        }

        ComRef<T>? activated = ComRef.TryTake<T>(pointer);
        if (activated is null)
        {
            hresult = CoreAudioErrors.NoInterface;
        }

        return activated;
    }

    /// <summary>
    /// Turns the controller property ("{1}.USB\VID_1234&amp;PID_5678&amp;MI_00\9&amp;...") into a PnP instance ID by
    /// removing the "{n}." prefix. A value without the prefix is returned as is; empty values give null.
    /// </summary>
    public static string? ParseControllerDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        if (text.StartsWith('{'))
        {
            int end = text.IndexOf("}.", StringComparison.Ordinal);
            if (end > 0)
            {
                text = text[(end + 2)..];
            }
        }

        return text.Length == 0 ? null : text;
    }
}
