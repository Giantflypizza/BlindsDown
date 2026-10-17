using BlindsDown.Core.Usage;

namespace BlindsDown.Usage;

/// <summary>Turns a process ID into a human-friendly app identity ("Discord", "Microsoft Teams"...).</summary>
internal interface IProcessIdentityResolver
{
    /// <summary>
    /// Never throws. For processes that can't be inspected (e.g. system services) it still returns
    /// the best available name, falling back to "Unknown app (PID 1234)".
    /// Results are cached by process ID plus start time, so a reused PID is never mislabelled.
    /// </summary>
    AppIdentity Resolve(int processId);

    /// <summary>True when a process with this exact exe path is running right now.</summary>
    bool IsExeRunning(string exePath);

    /// <summary>True when any process of this Store package family is running right now.</summary>
    bool IsPackageRunning(string packageFamilyName);
}
