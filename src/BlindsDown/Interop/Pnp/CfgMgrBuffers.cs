using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Interop.Pnp;

/// <summary>
/// The two-call buffer patterns of CfgMgr32, kept apart from the native calls so they can be tested:
///  * Lists: ask for the size, then read. A device can arrive in between, so the read fails with CR_BUFFER_SMALL
///    and the whole thing is simply repeated.
///  * Properties: read into a buffer; CR_BUFFER_SMALL reports the size needed (which can also change in between).
/// </summary>
internal static class CfgMgrBuffers
{
    /// <summary>Retries for a list or property that keeps growing; in practice the second attempt succeeds.</summary>
    public const int MaxAttempts = 5;

    /// <summary>A sanity limit (8 MB of text). Real interface lists are a few kilobytes.</summary>
    public const uint MaxListChars = 4 * 1024 * 1024;

    /// <summary>A sanity limit for one property value. Names and IDs are well under 1 KB.</summary>
    public const uint MaxPropertyBytes = 64 * 1024;

    /// <summary>Most properties (names, IDs, GUIDs, flags) fit in the first try.</summary>
    private const int FirstPropertyBytes = 512;

    /// <summary>
    /// Reads a multi-sz list. <paramref name="querySize"/> returns the size in characters (with the final double
    /// null); <paramref name="read"/> fills a buffer of that size. Returns the first CONFIGRET that isn't
    /// CR_SUCCESS (and an empty list), or CR_BUFFER_SMALL if the list kept growing.
    /// </summary>
    public static uint ReadList(Func<(uint Cr, uint Length)> querySize, Func<char[], uint> read, out IReadOnlyList<string> items)
    {
        items = [];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            (uint cr, uint length) = querySize();
            if (cr != CrSuccess)
            {
                return cr;
            }

            if (length > MaxListChars)
            {
                return CrInvalidData;
            }

            char[] buffer = new char[Math.Max(length, 1)];
            cr = read(buffer);
            if (cr == CrBufferSmall)
            {
                continue;
            }

            if (cr != CrSuccess)
            {
                return cr;
            }

            items = ParseMultiSz(buffer);
            return CrSuccess;
        }

        return CrBufferSmall;
    }

    /// <summary>
    /// Reads one property. <paramref name="read"/> gets a buffer and returns the CONFIGRET, the DEVPROPTYPE and
    /// the size: written on success, needed on CR_BUFFER_SMALL. <paramref name="value"/> is exactly the bytes written.
    /// </summary>
    public static uint ReadProperty(Func<byte[], (uint Cr, uint Type, uint Size)> read, out uint type, out byte[] value)
    {
        type = 0;
        value = [];
        byte[] buffer = new byte[FirstPropertyBytes];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            (uint cr, uint propertyType, uint size) = read(buffer);
            if (cr == CrBufferSmall)
            {
                if (size <= buffer.Length || size > MaxPropertyBytes)
                {
                    return CrInvalidData;
                }

                buffer = new byte[size];
                continue;
            }

            if (cr != CrSuccess)
            {
                return cr;
            }

            if (size > buffer.Length)
            {
                return CrInvalidData;
            }

            type = propertyType;
            value = buffer[..(int)size];
            return CrSuccess;
        }

        return CrBufferSmall;
    }

    /// <summary>
    /// Splits a REG_MULTI_SZ-style buffer ("a\0b\0\0"). Stops at the first empty string, which is how the list
    /// ends; a missing final terminator is tolerated.
    /// </summary>
    public static List<string> ParseMultiSz(ReadOnlySpan<char> buffer)
    {
        var items = new List<string>();
        while (!buffer.IsEmpty)
        {
            int end = buffer.IndexOf('\0');
            ReadOnlySpan<char> item = end < 0 ? buffer : buffer[..end];
            if (item.IsEmpty)
            {
                break;
            }

            items.Add(item.ToString());
            buffer = end < 0 ? [] : buffer[(end + 1)..];
        }

        return items;
    }

    /// <summary>A DEVPROP_TYPE_STRING value (UTF-16, null-terminated). Null for another type or an empty string.</summary>
    public static string? DecodeString(uint type, byte[] value)
    {
        if (type != DevPropTypeString || value.Length < sizeof(char))
        {
            return null;
        }

        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(value.AsSpan(0, value.Length & ~1));
        int end = text.IndexOf('\0');
        string result = (end < 0 ? text : text[..end]).ToString();
        return result.Length == 0 ? null : result;
    }

    public static Guid? DecodeGuid(uint type, byte[] value) =>
        type == DevPropTypeGuid && value.Length == 16 ? new Guid(value) : null;

    /// <summary>DEVPROP_BOOLEAN is one byte: DEVPROP_TRUE (0xFF) or DEVPROP_FALSE (0).</summary>
    public static bool? DecodeBoolean(uint type, byte[] value) =>
        type == DevPropTypeBoolean && value.Length == 1 ? value[0] != 0 : null;

    public static uint? DecodeUInt32(uint type, byte[] value) =>
        type == DevPropTypeUInt32 && value.Length == sizeof(uint) ? BinaryPrimitives.ReadUInt32LittleEndian(value) : null;
}
