using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>The (few) flat Windows functions the Core Audio layer needs.</summary>
internal static partial class CoreAudioNative
{
    /// <summary>
    /// Message for COM methods declared only so that the methods after them land in the right vtable slot.
    /// Marking them obsolete-as-error makes "BlindsDown never changes volume levels or creates audio sessions"
    /// a compile-time guarantee rather than a promise.
    /// </summary>
    public const string VtableSlotOnly = "Declared only to keep the COM vtable order. BlindsDown never calls this.";

    /// <summary>Frees whatever a PROPVARIANT owns (strings, CLSID buffers...) and resets it to VT_EMPTY.</summary>
    [LibraryImport("ole32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int PropVariantClear(ref PropVariant variant);
}
