using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>PROPERTYKEY: a property set GUID plus a property number.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>The endpoint properties BlindsDown reads. All are strings (VT_LPWSTR).</summary>
internal static class EndpointPropertyKeys
{
    /// <summary>PKEY_Device_FriendlyName: the full name, e.g. "Microphone (Arctis Nova Pro Wireless)".</summary>
    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>PKEY_Device_DeviceDesc: the endpoint's own name without the adapter, e.g. "Microphone".</summary>
    public static readonly PropertyKey DeviceDescription = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    /// <summary>PKEY_DeviceInterface_FriendlyName: the adapter's name, e.g. "SteelSeries Sonar Virtual Audio Device".</summary>
    public static readonly PropertyKey AdapterName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    /// <summary>
    /// The PnP instance ID of the hardware behind the endpoint, as "{1}." + instance ID. Not in the public
    /// headers, but stable since Windows Vista and, unlike the endpoint's PnP parent, readable in every state
    /// (including NOTPRESENT), so exclusions keep working while a device is unplugged.
    /// </summary>
    public static readonly PropertyKey ControllerDeviceId = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
}

/// <summary>
/// PROPVARIANT, as far as BlindsDown reads it: a 2-byte type tag, 6 reserved bytes, then a union whose
/// largest members (BLOB and friends) are two pointers, which makes it 24 bytes on 64-bit Windows.
/// BlindsDown never runs as a 32-bit process (where it would be 16 bytes; a 24-byte buffer is still large
/// enough). The value must be freed with <see cref="CoreAudioNative.PropVariantClear"/> after reading.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtEmpty = 0;
    public const ushort VtBool = 11;
    public const ushort VtUInt32 = 19;
    public const ushort VtLpwstr = 31;
    public const ushort VtClsid = 72;

    [FieldOffset(0)]
    internal ushort VarType;

    /// <summary>pwszVal, puuid and every other pointer member of the union.</summary>
    [FieldOffset(8)]
    internal nint Pointer;

    /// <summary>ulVal (VT_UI4).</summary>
    [FieldOffset(8)]
    internal uint UInt32Value;

    /// <summary>boolVal (VT_BOOL): a VARIANT_BOOL, -1 for true and 0 for false.</summary>
    [FieldOffset(8)]
    internal short BoolValue;

    /// <summary>The string for VT_LPWSTR, otherwise null.</summary>
    public readonly string? AsString() =>
        VarType == VtLpwstr && Pointer != 0 ? Marshal.PtrToStringUni(Pointer) : null;

    /// <summary>The number for VT_UI4, otherwise null.</summary>
    public readonly uint? AsUInt32() => VarType == VtUInt32 ? UInt32Value : null;

    /// <summary>The value for VT_BOOL, otherwise null. Any non-zero VARIANT_BOOL counts as true.</summary>
    public readonly bool? AsBoolean() => VarType == VtBool ? BoolValue != 0 : null;

    /// <summary>The GUID for VT_CLSID (which stores a pointer to the GUID), otherwise null.</summary>
    public readonly unsafe Guid? AsGuid() =>
        VarType == VtClsid && Pointer != 0 ? *(Guid*)Pointer : null;
}

/// <summary>
/// IPropertyStore. BlindsDown opens stores with STGM_READ only, so the setters would fail anyway;
/// they are declared solely to keep the vtable order and cannot be called from our code.
/// </summary>
[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [Obsolete(CoreAudioNative.VtableSlotOnly, error: true)]
    [PreserveSig]
    int Commit();
}

/// <summary>Reads typed values from an <see cref="IPropertyStore"/>, always freeing the PROPVARIANT.</summary>
internal static class PropertyStoreReader
{
    /// <summary>The VT_LPWSTR value of <paramref name="key"/>, or null when it is missing, empty or of another type.</summary>
    public static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant value) < 0)
        {
            return null;
        }

        try
        {
            string? text = value.AsString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            _ = CoreAudioNative.PropVariantClear(ref value);
        }
    }
}
