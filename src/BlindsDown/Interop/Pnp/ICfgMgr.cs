using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Interop.Pnp;

/// <summary>
/// The CfgMgr32 calls the Plug and Play layer makes. It is an interface so that the decisions on top of it (which
/// devices are cameras, how a disable is verified, what a failure means) are tested with a fake: a real disable
/// needs administrator rights and real hardware, and must never run on a developer's PC. Methods return a
/// CONFIGRET (or null for "no such value") and never throw for Windows errors. Implementations need no
/// particular thread.
/// </summary>
internal interface ICfgMgr
{
    /// <summary>Interface paths of every interface of <paramref name="interfaceClass"/>, including those of
    /// disabled and unplugged devices.</summary>
    uint GetDeviceInterfaces(Guid interfaceClass, out IReadOnlyList<string> interfacePaths);

    /// <summary>The instance ID of the device that exposes the interface, or null when it can't be read.</summary>
    string? GetInterfaceInstanceId(string interfacePath);

    /// <summary>Finds a device. Without <paramref name="includeNotPresent"/>, an unplugged device is
    /// CR_NO_SUCH_DEVNODE. An invalid ID (empty, too long, embedded null) is CR_INVALID_DEVICE_ID.</summary>
    uint LocateDevNode(string instanceId, bool includeNotPresent, out uint devInst);

    /// <summary>DN_* status bits and the CM_PROB_* problem code. Fails for a device that isn't connected.</summary>
    uint GetStatus(uint devInst, out uint status, out uint problem);

    string? GetString(uint devInst, DevPropKey key);

    Guid? GetGuid(uint devInst, DevPropKey key);

    bool? GetBoolean(uint devInst, DevPropKey key);

    uint? GetUInt32(uint devInst, DevPropKey key);

    /// <summary>CM_Disable_DevNode. Needs administrator rights.</summary>
    uint DisableDevNode(uint devInst, uint flags);

    /// <summary>CM_Enable_DevNode. Needs administrator rights.</summary>
    uint EnableDevNode(uint devInst);
}
