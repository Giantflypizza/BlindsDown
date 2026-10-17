using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>Strings that Core Audio returns in CoTaskMemAlloc'ed memory, which the caller must free.</summary>
internal static class CoTaskMemText
{
    /// <summary>
    /// Copies the string and frees the native memory. Call only when the method succeeded (on failure the
    /// out value is not guaranteed to be set). Returns null for a null pointer.
    /// </summary>
    public static string? Take(nint text)
    {
        if (text == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }
}
