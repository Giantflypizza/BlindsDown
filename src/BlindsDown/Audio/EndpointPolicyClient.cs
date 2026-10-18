using System.Runtime.InteropServices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Audio;

/// <summary>
/// BlindsDown's connection to the Sound settings switches (<see cref="IPolicyConfig"/>), created on first use and
/// created again after the Windows audio service restarted (objects from before a restart keep failing with RPC
/// errors). Every method returns the HRESULT instead of throwing, including when the object can't be created
/// (REGDB_E_CLASSNOTREG, E_NOINTERFACE), so <see cref="EndpointSwitch"/> decides what each failure means.
///
/// Like <see cref="EndpointEnumerator"/>, the wrapper is never released explicitly: the runtime may hand the same
/// wrapper to another component, and dropping it lets the garbage collector release it.
///
/// Use only on the engine thread.
/// </summary>
internal sealed class EndpointPolicyClient
{
    private IPolicyConfig? _policy;

    /// <summary>IPolicyConfig::SetEndpointVisibility: the "Allow" / "Don't allow" switch.</summary>
    public int SetEndpointVisibility(string endpointId, bool visible) =>
        Call(policy => policy.SetEndpointVisibility(endpointId, visible ? 1 : 0));

    /// <summary>IPolicyConfig::SetDefaultEndpoint for one role.</summary>
    public int SetDefaultEndpoint(string endpointId, DeviceRole role) =>
        Call(policy => policy.SetDefaultEndpoint(endpointId, role));

    /// <summary>Drops the current object; the next call creates a fresh one.</summary>
    public void Reset() => _policy = null;

    private int Call(Func<IPolicyConfig, int> call)
    {
        int hr = Current(out IPolicyConfig? policy);
        if (policy is null)
        {
            return hr;
        }

        hr = call(policy);
        if (CoreAudioErrors.IsServiceFailure(hr))
        {
            // Made before the audio service restarted: it will never work again.
            Reset();
        }

        return hr;
    }

    private int Current(out IPolicyConfig? policy)
    {
        policy = _policy;
        if (policy is not null)
        {
            return CoreAudioErrors.SOk;
        }

        try
        {
            // CoCreateInstance, then a QueryInterface for IPolicyConfig (the type test).
            object instance = new PolicyConfigClientCoClass();
            if (instance is not IPolicyConfig created)
            {
                return CoreAudioErrors.NoInterface;
            }

            _policy = policy = created;
            return CoreAudioErrors.SOk;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex.HResult;
        }
    }
}
