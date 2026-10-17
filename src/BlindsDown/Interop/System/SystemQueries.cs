using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Interop.SystemApis;

/// <summary>A process as listed by the kernel: ID, creation time (FILETIME) and image file name (no path).</summary>
internal readonly record struct ProcessEntry(int ProcessId, long CreateTime, string ImageName);

/// <summary>
/// Read-only process, package, device-name and service queries on top of <see cref="SystemNative"/>.
/// None of these throw for Windows errors: failures return null or an empty list, because callers use
/// them only to make names friendlier and must degrade to "Unknown app" rather than fail.
/// </summary>
internal static unsafe class SystemQueries
{
    /// <summary>The kernel caps UNICODE_STRING at 32767 characters.</summary>
    private const int MaxUnicodeStringChars = short.MaxValue;

    /// <summary>Opens a process for limited queries. Returns 0 when that is not allowed (typically SYSTEM
    /// and other users' processes) or the process is gone. Close the handle with <see cref="CloseProcess"/>.</summary>
    public static nint OpenProcessForQuery(int processId) =>
        processId <= 0 ? 0 : SystemNative.OpenProcess(SystemNative.ProcessQueryLimitedInformation, 0, (uint)processId);

    public static void CloseProcess(nint process)
    {
        if (process != 0)
        {
            _ = SystemNative.CloseHandle(process);
        }
    }

    /// <summary>The process creation time (FILETIME, UTC). Together with the PID it identifies a process
    /// uniquely, because Windows reuses PIDs quickly.</summary>
    public static long? GetCreationTime(nint process)
    {
        long creation;
        long exit;
        long kernel;
        long user;
        return SystemNative.GetProcessTimes(process, &creation, &exit, &kernel, &user) != 0 ? creation : null;
    }

    /// <summary>Win32 path of the process's exe ("C:\...\app.exe").</summary>
    public static string? GetImagePath(nint process)
    {
        const int SmallChars = 1024;
        char* small = stackalloc char[SmallChars];
        uint size = SmallChars;
        if (SystemNative.QueryFullProcessImageNameW(process, 0, small, &size) != 0)
        {
            return new string(small, 0, (int)size);
        }

        if (Marshal.GetLastPInvokeError() != SystemNative.ErrorInsufficientBuffer)
        {
            return null;
        }

        char[] large = new char[MaxUnicodeStringChars];
        fixed (char* p = large)
        {
            size = (uint)large.Length;
            return SystemNative.QueryFullProcessImageNameW(process, 0, p, &size) != 0 ? new string(p, 0, (int)size) : null;
        }
    }

    /// <summary>The Store package family of the process, or null for classic desktop apps
    /// (APPMODEL_ERROR_NO_PACKAGE, 15700) and on errors.</summary>
    public static string? GetPackageFamilyName(nint process)
    {
        // PACKAGE_FAMILY_NAME_MAX_LENGTH is 64 + 1 + 13; 256 leaves plenty of room.
        const int Capacity = 256;
        char* buffer = stackalloc char[Capacity];
        uint length = Capacity;
        return SystemNative.GetPackageFamilyName(process, &length, buffer) == SystemNative.ErrorSuccess ? NonEmpty(new string(buffer)) : null;
    }

    /// <summary>The exact package (with version and architecture) the process runs from, or null.</summary>
    public static string? GetPackageFullName(nint process)
    {
        // PACKAGE_FULL_NAME_MAX_LENGTH is 127.
        const int Capacity = 256;
        char* buffer = stackalloc char[Capacity];
        uint length = Capacity;
        return SystemNative.GetPackageFullName(process, &length, buffer) == SystemNative.ErrorSuccess ? NonEmpty(new string(buffer)) : null;
    }

    /// <summary>The folder a package is installed in (where its AppxManifest.xml lives), or null.</summary>
    public static string? GetPackagePath(string packageFullName)
    {
        fixed (char* name = packageFullName)
        {
            uint length = 0;
            int result = SystemNative.GetPackagePathByFullName(name, &length, null);
            if (result != SystemNative.ErrorInsufficientBuffer || length == 0 || length > MaxUnicodeStringChars)
            {
                return null;
            }

            char[] buffer = new char[length];
            fixed (char* p = buffer)
            {
                return SystemNative.GetPackagePathByFullName(name, &length, p) == SystemNative.ErrorSuccess ? NonEmpty(new string(p)) : null;
            }
        }
    }

    /// <summary>Full names of every installed package in a family (main package, resource packages,
    /// other architectures). Empty when the family is not installed for this user.</summary>
    public static IReadOnlyList<string> GetPackageFullNames(string packageFamilyName)
    {
        fixed (char* family = packageFamilyName)
        {
            // Retried because a package can be installed between the size query and the real call.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                uint bufferLength = 0;
                int result = SystemNative.GetPackagesByPackageFamily(family, &count, null, &bufferLength, null);
                if (result != SystemNative.ErrorInsufficientBuffer || count == 0 || bufferLength == 0)
                {
                    return [];
                }

                nint[] names = new nint[count];
                char[] buffer = new char[bufferLength];
                fixed (nint* n = names)
                fixed (char* b = buffer)
                {
                    result = SystemNative.GetPackagesByPackageFamily(family, &count, (char**)n, &bufferLength, b);
                    if (result == SystemNative.ErrorInsufficientBuffer)
                    {
                        continue;
                    }

                    if (result != SystemNative.ErrorSuccess)
                    {
                        return [];
                    }

                    var list = new List<string>((int)count);
                    for (int i = 0; i < count; i++)
                    {
                        if (n[i] != 0)
                        {
                            list.Add(new string((char*)n[i]));
                        }
                    }

                    return list;
                }
            }

            return [];
        }
    }

    /// <summary>
    /// The kernel's image path ("\Device\HarddiskVolume3\Windows\System32\svchost.exe") for any process ID,
    /// without opening the process. Null if the process is gone or has no image file (System, Registry).
    /// </summary>
    public static string? GetImageNtPath(int processId)
    {
        int chars = 512;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            char[] buffer = new char[chars];
            fixed (char* b = buffer)
            {
                SystemNative.SystemProcessIdInformation info = default;
                info.ProcessId = processId;
                info.ImageName.Length = 0;
                info.ImageName.MaximumLength = (ushort)(chars * 2);
                info.ImageName.Buffer = b;
                int status = SystemNative.NtQuerySystemInformation(
                    SystemNative.SystemProcessIdInformationClass, &info, (uint)sizeof(SystemNative.SystemProcessIdInformation), null);
                if (status == SystemNative.StatusInfoLengthMismatch && info.ImageName.MaximumLength > chars * 2)
                {
                    chars = Math.Min(info.ImageName.MaximumLength / 2, MaxUnicodeStringChars);
                    continue;
                }

                if (status < 0 || info.ImageName.Length == 0)
                {
                    return null;
                }

                return new string(b, 0, info.ImageName.Length / 2);
            }
        }

        return null;
    }

    /// <summary>
    /// Every process in the system with its creation time, in one kernel call. Unlike OpenProcess this
    /// works for every process, so it is how processes we may not open still get a reuse-safe identity.
    /// Null on failure.
    /// </summary>
    public static List<ProcessEntry>? ListProcesses()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                fixed (byte* p = buffer)
                {
                    uint needed = 0;
                    int status = SystemNative.NtQuerySystemInformation(SystemNative.SystemProcessInformationClass, p, (uint)buffer.Length, &needed);
                    if (status == SystemNative.StatusInfoLengthMismatch || status == SystemNative.StatusBufferTooSmall)
                    {
                        // Processes come and go between calls, so ask for some headroom.
                        size = (int)Math.Min(Math.Max((long)buffer.Length * 2, (long)needed + (64 * 1024)), 256L * 1024 * 1024);
                        continue;
                    }

                    return status < 0 ? null : ParseProcessList(p, buffer.Length);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>Display names of the Win32 services running inside <paramref name="processId"/>
    /// (svchost hosts several). Empty when there are none or the service list can't be read.</summary>
    public static IReadOnlyList<string> GetServiceDisplayNames(int processId)
    {
        var names = new List<string>();
        nint manager = SystemNative.OpenSCManagerW(null, null, SystemNative.ScManagerConnect | SystemNative.ScManagerEnumerateService);
        if (manager == 0)
        {
            return names;
        }

        try
        {
            // The API returns at most 256 KB per call and continues from resumeHandle.
            uint resumeHandle = 0;
            int size = 64 * 1024;
            for (int round = 0; round < 32; round++)
            {
                byte[] buffer = new byte[size];
                fixed (byte* b = buffer)
                {
                    uint needed = 0;
                    uint returned = 0;
                    int ok = SystemNative.EnumServicesStatusExW(
                        manager,
                        SystemNative.ScEnumProcessInfo,
                        SystemNative.ServiceWin32,
                        SystemNative.ServiceActive,
                        b,
                        (uint)size,
                        &needed,
                        &returned,
                        &resumeHandle,
                        null);
                    int error = ok != 0 ? SystemNative.ErrorSuccess : Marshal.GetLastPInvokeError();
                    var entries = (SystemNative.EnumServiceStatusProcess*)b;
                    for (uint i = 0; i < returned; i++)
                    {
                        if (entries[i].ServiceStatus.ProcessId == (uint)processId && entries[i].DisplayName != null)
                        {
                            names.Add(new string(entries[i].DisplayName));
                        }
                    }

                    if (ok != 0 || error != SystemNative.ErrorMoreData)
                    {
                        break;
                    }

                    if (returned == 0)
                    {
                        size = Math.Max(size * 2, (int)Math.Min(needed, 256 * 1024));
                    }
                }
            }
        }
        finally
        {
            _ = SystemNative.CloseServiceHandle(manager);
        }

        return names;
    }

    /// <summary>"C:" -> "\Device\HarddiskVolume3" for every drive letter (SUBST drives map to "\??\C:\folder",
    /// network drives to a redirector device). Pure object-manager lookups: no disk or network I/O.</summary>
    public static IReadOnlyList<(string Drive, string Device)> GetDriveDevices()
    {
        var map = new List<(string Drive, string Device)>();
        string[] roots;
        try
        {
            roots = Environment.GetLogicalDrives();
        }
        catch (IOException)
        {
            return map;
        }
        catch (UnauthorizedAccessException)
        {
            return map;
        }

        const int Capacity = 1024;
        char* target = stackalloc char[Capacity];
        foreach (string root in roots)
        {
            if (root.Length < 2 || root[1] != ':')
            {
                continue;
            }

            string drive = root[..2];
            fixed (char* name = drive)
            {
                // The result is a list of null-terminated strings; the first is the current mapping.
                if (SystemNative.QueryDosDeviceW(name, target, Capacity) != 0)
                {
                    map.Add((drive, new string(target)));
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Arms a one-shot, asynchronous change notification on <paramref name="key"/> (and its subtree) that
    /// signals <paramref name="signal"/>. Returns the Win32 error code. Both SafeHandles are AddRef'd for the
    /// call, so a concurrent Dispose can't close (and Windows can't recycle) a handle while it is in use.
    /// </summary>
    public static int NotifyKeyChange(SafeRegistryHandle key, uint notifyFilter, SafeWaitHandle signal)
    {
        bool keyAdded = false;
        bool signalAdded = false;
        try
        {
            key.DangerousAddRef(ref keyAdded);
            signal.DangerousAddRef(ref signalAdded);
            return SystemNative.RegNotifyChangeKeyValue(key.DangerousGetHandle(), 1, notifyFilter, signal.DangerousGetHandle(), 1);
        }
        finally
        {
            if (signalAdded)
            {
                signal.DangerousRelease();
            }

            if (keyAdded)
            {
                key.DangerousRelease();
            }
        }
    }

    /// <summary>Resolves an indirect string such as "@{Package_1.0_x64__id?ms-resource://Name/Resources/Key}".</summary>
    public static string? LoadIndirectString(string source)
    {
        const int Capacity = 1024;
        char* output = stackalloc char[Capacity];
        output[0] = '\0';
        fixed (char* s = source)
        {
            return SystemNative.SHLoadIndirectString(s, output, Capacity, 0) < 0 ? null : NonEmpty(new string(output));
        }
    }

    private static List<ProcessEntry> ParseProcessList(byte* buffer, int length)
    {
        var list = new List<ProcessEntry>(512);
        long offset = 0;
        while (offset + sizeof(SystemNative.SystemProcessInformation) <= length)
        {
            var info = (SystemNative.SystemProcessInformation*)(buffer + offset);
            string name = info->ImageName.Buffer == null || info->ImageName.Length == 0
                ? string.Empty
                : new string(info->ImageName.Buffer, 0, info->ImageName.Length / 2);
            list.Add(new ProcessEntry((int)info->UniqueProcessId, info->CreateTime, name));
            if (info->NextEntryOffset == 0)
            {
                break;
            }

            offset += info->NextEntryOffset;
        }

        return list;
    }

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;
}
