using System.Text;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Tests.Pnp;

public sealed class CfgMgrBuffersTests
{
    [Fact]
    public void MultiSzListsAreSplitAtNulls()
    {
        Assert.Empty(CfgMgrBuffers.ParseMultiSz(string.Empty));
        Assert.Empty(CfgMgrBuffers.ParseMultiSz("\0"));
        Assert.Empty(CfgMgrBuffers.ParseMultiSz("\0\0"));
        Assert.Equal(["a"], CfgMgrBuffers.ParseMultiSz("a\0\0"));
        Assert.Equal(["first", "second"], CfgMgrBuffers.ParseMultiSz("first\0second\0\0"));

        // A missing final terminator is tolerated; anything after the empty string that ends the list is ignored.
        Assert.Equal(["first", "second"], CfgMgrBuffers.ParseMultiSz("first\0second"));
        Assert.Equal(["first"], CfgMgrBuffers.ParseMultiSz("first\0\0stale\0\0"));
    }

    [Fact]
    public void AListThatGrowsBetweenTheSizeQueryAndTheReadIsReadAgain()
    {
        // A webcam arrives between the two calls: the list Windows has is longer than the size first reported.
        string[] lists = ["one\0\0", "one\0two\0\0"];
        int sizeQueries = 0;
        int reads = 0;

        uint cr = CfgMgrBuffers.ReadList(
            () => (CrSuccess, (uint)lists[Math.Min(sizeQueries++, 1)].Length),
            buffer =>
            {
                string current = lists[1];
                reads++;
                if (buffer.Length < current.Length)
                {
                    return CrBufferSmall;
                }

                current.CopyTo(buffer);
                return CrSuccess;
            },
            out IReadOnlyList<string> items);

        Assert.Equal(CrSuccess, cr);
        Assert.Equal(["one", "two"], items);
        Assert.Equal(2, sizeQueries);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void AListThatKeepsGrowingGivesUpWithBufferSmall()
    {
        int reads = 0;

        uint cr = CfgMgrBuffers.ReadList(() => (CrSuccess, 4), _ => { reads++; return CrBufferSmall; }, out IReadOnlyList<string> items);

        Assert.Equal(CrBufferSmall, cr);
        Assert.Equal(CfgMgrBuffers.MaxAttempts, reads);
        Assert.Empty(items);
    }

    [Fact]
    public void ListErrorsArePassedThrough()
    {
        Assert.Equal(CrAccessDenied, CfgMgrBuffers.ReadList(() => (CrAccessDenied, 0), _ => CrSuccess, out IReadOnlyList<string> none));
        Assert.Empty(none);
        Assert.Equal(CrFailure, CfgMgrBuffers.ReadList(() => (CrSuccess, 3), _ => CrFailure, out none));
        Assert.Empty(none);
        Assert.Equal(CrInvalidData, CfgMgrBuffers.ReadList(() => (CrSuccess, CfgMgrBuffers.MaxListChars + 1), _ => CrSuccess, out none));
    }

    [Fact]
    public void AnEmptyListIsOneNull()
    {
        Assert.Equal(CrSuccess, CfgMgrBuffers.ReadList(() => (CrSuccess, 1), _ => CrSuccess, out IReadOnlyList<string> items));
        Assert.Empty(items);

        // Some Windows versions report 0 for an empty list; the buffer still has room for the terminator.
        Assert.Equal(CrSuccess, CfgMgrBuffers.ReadList(() => (CrSuccess, 0), buffer => buffer.Length == 1 ? CrSuccess : CrFailure, out items));
        Assert.Empty(items);
    }

    [Fact]
    public void APropertyLargerThanTheFirstBufferIsReadIntoOneOfTheRightSize()
    {
        byte[] text = Encoding.Unicode.GetBytes(new string('x', 600) + "\0");
        var sizes = new List<int>();

        uint cr = CfgMgrBuffers.ReadProperty(
            buffer =>
            {
                sizes.Add(buffer.Length);
                if (buffer.Length < text.Length)
                {
                    return (CrBufferSmall, DevPropTypeString, (uint)text.Length);
                }

                text.CopyTo(buffer, 0);
                return (CrSuccess, DevPropTypeString, (uint)text.Length);
            },
            out uint type,
            out byte[] value);

        Assert.Equal(CrSuccess, cr);
        Assert.Equal(DevPropTypeString, type);
        Assert.Equal(text, value);
        Assert.Equal([512, text.Length], sizes);
        Assert.Equal(new string('x', 600), CfgMgrBuffers.DecodeString(type, value));
    }

    [Fact]
    public void APropertyIsExactlyTheBytesWindowsWrote()
    {
        uint cr = CfgMgrBuffers.ReadProperty(buffer => { buffer[0] = 0xFF; return (CrSuccess, DevPropTypeBoolean, 1); }, out uint type, out byte[] value);

        Assert.Equal(CrSuccess, cr);
        Assert.Equal([0xFF], value);
        Assert.True(CfgMgrBuffers.DecodeBoolean(type, value));
    }

    [Fact]
    public void PropertyErrorsArePassedThroughAndNonsenseSizesRefused()
    {
        Assert.Equal(CrNoSuchValue, CfgMgrBuffers.ReadProperty(_ => (CrNoSuchValue, 0, 0), out _, out byte[] value));
        Assert.Empty(value);

        // "Too small" with a size that isn't larger, or absurdly large, or a size larger than the buffer on success.
        Assert.Equal(CrInvalidData, CfgMgrBuffers.ReadProperty(buffer => (CrBufferSmall, DevPropTypeString, (uint)buffer.Length), out _, out _));
        Assert.Equal(CrInvalidData, CfgMgrBuffers.ReadProperty(_ => (CrBufferSmall, DevPropTypeString, CfgMgrBuffers.MaxPropertyBytes + 1), out _, out _));
        Assert.Equal(CrInvalidData, CfgMgrBuffers.ReadProperty(buffer => (CrSuccess, DevPropTypeString, (uint)buffer.Length + 1), out _, out _));
    }

    [Fact]
    public void PropertyValuesAreDecodedOnlyWithTheRightType()
    {
        byte[] text = Encoding.Unicode.GetBytes("USB Webcam\0");
        Assert.Equal("USB Webcam", CfgMgrBuffers.DecodeString(DevPropTypeString, text));
        Assert.Equal("ab", CfgMgrBuffers.DecodeString(DevPropTypeString, Encoding.Unicode.GetBytes("ab"))); // No terminator.
        Assert.Equal("ab", CfgMgrBuffers.DecodeString(DevPropTypeString, [.. Encoding.Unicode.GetBytes("ab"), 0x41])); // Odd length.
        Assert.Null(CfgMgrBuffers.DecodeString(DevPropTypeString, Encoding.Unicode.GetBytes("\0")));
        Assert.Null(CfgMgrBuffers.DecodeString(DevPropTypeString, []));
        Assert.Null(CfgMgrBuffers.DecodeString(DevPropTypeGuid, text));

        var guid = Guid.NewGuid();
        Assert.Equal(guid, CfgMgrBuffers.DecodeGuid(DevPropTypeGuid, guid.ToByteArray()));
        Assert.Null(CfgMgrBuffers.DecodeGuid(DevPropTypeString, guid.ToByteArray()));
        Assert.Null(CfgMgrBuffers.DecodeGuid(DevPropTypeGuid, new byte[15]));

        Assert.True(CfgMgrBuffers.DecodeBoolean(DevPropTypeBoolean, [0xFF]));
        Assert.False(CfgMgrBuffers.DecodeBoolean(DevPropTypeBoolean, [0x00]));
        Assert.Null(CfgMgrBuffers.DecodeBoolean(DevPropTypeUInt32, [0xFF]));
        Assert.Null(CfgMgrBuffers.DecodeBoolean(DevPropTypeBoolean, [0xFF, 0]));

        Assert.Equal(0x01020304u, CfgMgrBuffers.DecodeUInt32(DevPropTypeUInt32, [4, 3, 2, 1]));
        Assert.Null(CfgMgrBuffers.DecodeUInt32(DevPropTypeUInt32, [4, 3, 2]));
        Assert.Null(CfgMgrBuffers.DecodeUInt32(DevPropTypeBoolean, [4, 3, 2, 1]));
    }

    [Fact]
    public void ConfigRetsAreNamedForTheLog()
    {
        Assert.Equal("CR_REMOVE_VETOED (0x17)", CfgMgrErrors.Describe(CrRemoveVetoed));
        Assert.Equal("CR_ACCESS_DENIED (0x33)", CfgMgrErrors.Describe(CrAccessDenied));
        Assert.Equal("CONFIGRET 0x2A", CfgMgrErrors.Describe(0x2A));

        Assert.True(CfgMgrErrors.IsGone(CrNoSuchDevNode));
        Assert.True(CfgMgrErrors.IsGone(CrInvalidDevNode));
        Assert.True(CfgMgrErrors.IsGone(CrDeviceNotThere));
        Assert.False(CfgMgrErrors.IsGone(CrSuccess));
        Assert.False(CfgMgrErrors.IsGone(CrAccessDenied));
    }

    [Fact]
    public void TheNotificationFilterHasTheSizeWindowsExpects()
    {
        // CM_NOTIFY_FILTER: four DWORDs, then a union whose largest member is WCHAR[MAX_DEVICE_ID_LEN].
        Assert.Equal(416, CmNotifyFilter.StructSize);
        Assert.Equal(416, System.Runtime.CompilerServices.Unsafe.SizeOf<CmNotifyFilter>());
        Assert.Equal(20, System.Runtime.CompilerServices.Unsafe.SizeOf<DevPropKey>());
    }
}
