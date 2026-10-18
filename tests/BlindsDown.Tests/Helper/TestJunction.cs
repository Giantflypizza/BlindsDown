using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// A directory junction (what "mklink /J" makes), which needs no special rights, so tests can check that setup
/// code never follows links. Only ever made inside a test's own temp folder; disposing removes the link (never what
/// it points to), which .NET's own recursive delete can't do without administrator rights.
/// </summary>
internal sealed unsafe partial class TestJunction : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const nint InvalidHandleValue = -1;

    private readonly string _link;

    private TestJunction(string link) => _link = link;

    /// <summary>Creates <paramref name="link"/> as a junction to the existing folder <paramref name="target"/>.</summary>
    public static TestJunction Create(string link, string target)
    {
        Directory.CreateDirectory(link);

        // REPARSE_DATA_BUFFER, MountPointReparseBuffer: tag, data length, reserved, then the substitute name
        // ("\??\C:\...") and the print name, each followed by a terminating null.
        string substitute = @"\??\" + Path.GetFullPath(target);
        string print = Path.GetFullPath(target);
        int substituteBytes = substitute.Length * sizeof(char);
        int printBytes = print.Length * sizeof(char);
        int dataLength = 8 + substituteBytes + sizeof(char) + printBytes + sizeof(char);
        byte[] buffer = new byte[8 + dataLength];
        fixed (byte* b = buffer)
        fixed (char* s = substitute)
        fixed (char* p = print)
        {
            *(uint*)b = IoReparseTagMountPoint;
            *(ushort*)(b + 4) = (ushort)dataLength;
            *(ushort*)(b + 10) = (ushort)substituteBytes;
            *(ushort*)(b + 12) = (ushort)(substituteBytes + sizeof(char));
            *(ushort*)(b + 14) = (ushort)printBytes;
            Buffer.MemoryCopy(s, b + 16, substituteBytes, substituteBytes);
            Buffer.MemoryCopy(p, b + 16 + substituteBytes + sizeof(char), printBytes, printBytes);

            nint handle;
            fixed (char* name = link)
            {
                handle = CreateFileW(name, GenericWrite, 0, null, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            }

            if (handle == InvalidHandleValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                uint returned;
                if (DeviceIoControl(handle, FsctlSetReparsePoint, b, (uint)buffer.Length, null, 0, &returned, null) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }

        return new TestJunction(link);
    }

    public void Dispose()
    {
        if (Directory.Exists(_link))
        {
            Directory.Delete(_link, recursive: false);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    private static partial nint CreateFileW(char* fileName, uint access, uint share, void* security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static partial int DeviceIoControl(nint device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, uint* returned, void* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    private static partial int CloseHandle(nint handle);
}
