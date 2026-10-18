using System.Globalization;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Interop.Pnp;

/// <summary>Readable CONFIGRET codes for the log and for problem details.</summary>
internal static class CfgMgrErrors
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [CrSuccess] = "CR_SUCCESS",
        [CrOutOfMemory] = "CR_OUT_OF_MEMORY",
        [CrInvalidPointer] = "CR_INVALID_POINTER",
        [CrInvalidFlag] = "CR_INVALID_FLAG",
        [CrInvalidDevNode] = "CR_INVALID_DEVNODE",
        [CrNoSuchDevNode] = "CR_NO_SUCH_DEVNODE",
        [CrFailure] = "CR_FAILURE",
        [CrRemoveVetoed] = "CR_REMOVE_VETOED",
        [CrBufferSmall] = "CR_BUFFER_SMALL",
        [CrInvalidDeviceId] = "CR_INVALID_DEVICE_ID",
        [CrInvalidData] = "CR_INVALID_DATA",
        [CrNeedRestart] = "CR_NEED_RESTART",
        [CrDeviceNotThere] = "CR_DEVICE_NOT_THERE",
        [CrNoSuchValue] = "CR_NO_SUCH_VALUE",
        [CrNotDisableable] = "CR_NOT_DISABLEABLE",
        [CrAccessDenied] = "CR_ACCESS_DENIED",
        [CrCallNotImplemented] = "CR_CALL_NOT_IMPLEMENTED",
        [CrNoSuchDeviceInterface] = "CR_NO_SUCH_DEVICE_INTERFACE",
    };

    /// <summary>"CR_REMOVE_VETOED (0x17)"; unknown codes as "CONFIGRET 0x2A".</summary>
    public static string Describe(uint configRet) =>
        Names.TryGetValue(configRet, out string? name)
            ? string.Create(CultureInfo.InvariantCulture, $"{name} (0x{configRet:X2})")
            : string.Create(CultureInfo.InvariantCulture, $"CONFIGRET 0x{configRet:X2}");

    /// <summary>The device (or its devnode handle) no longer exists: it was unplugged or uninstalled.</summary>
    public static bool IsGone(uint configRet) => configRet is CrNoSuchDevNode or CrInvalidDevNode or CrDeviceNotThere;
}
