using BlindsDown.Interop.SystemApis;

namespace BlindsDown.Usage;

/// <summary>
/// Path rules for process images: turning kernel paths into normal ones, and deciding which files we may
/// open to read version info. BlindsDown never touches the network, and merely opening a file on a share
/// (or on a drive letter mapped to one) makes Windows talk SMB, so only local disks qualify.
/// </summary>
internal static class ImagePaths
{
    /// <summary>FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS:
    /// the file's content lives elsewhere (OneDrive / Cloud Files placeholders, HSM storage).</summary>
    private const FileAttributes RemoteContent = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private static readonly ExpiringCache<string, IReadOnlyList<(string Drive, string Device)>> DriveDevices =
        new(TimeSpan.FromMinutes(1), 1, StringComparer.Ordinal);

    /// <summary>
    /// True when opening the file can't cause network traffic: it is on a local disk (<see cref="IsLocalDrivePath(string)"/>)
    /// and its content is on that disk. A OneDrive placeholder is on C: too, but opening it makes the sync client
    /// download it; its attributes, unlike its content, are read without a recall. False when the file is missing.
    /// </summary>
    public static bool IsSafeToOpen(string path)
    {
        if (!IsLocalDrivePath(path))
        {
            return false;
        }

        try
        {
            return HasLocalContent(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool HasLocalContent(FileAttributes attributes) => (attributes & RemoteContent) == 0;

    /// <summary>"\Device\HarddiskVolume3\Windows\x.exe" -> "C:\Windows\x.exe"; null when it can't be mapped.</summary>
    public static string? ToDosPath(string ntPath)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? path = ToDosPath(ntPath, CurrentDriveDevices(refresh: false), windows);

        // A drive mounted since the map was cached (USB stick, VHD) would not be found; look once more.
        return path ?? ToDosPath(ntPath, CurrentDriveDevices(refresh: true), windows);
    }

    internal static string? ToDosPath(string ntPath, IReadOnlyList<(string Drive, string Device)> drives, string windowsDirectory)
    {
        if (ntPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            string rest = ntPath[4..];
            return rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + rest[4..] : rest;
        }

        const string SystemRoot = @"\SystemRoot\";
        if (ntPath.StartsWith(SystemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return windowsDirectory.Length == 0 ? null : Path.Join(windowsDirectory, ntPath[SystemRoot.Length..]);
        }

        const string Mup = @"\Device\Mup\";
        if (ntPath.StartsWith(Mup, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + ntPath[Mup.Length..];
        }

        foreach ((string drive, string device) in drives)
        {
            if (device.Length > 0
                && ntPath.Length > device.Length
                && ntPath[device.Length] == '\\'
                && ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return drive + ntPath[device.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// True only for "X:\..." paths on a local disk or optical drive. UNC paths, device paths and drive letters
    /// backed by a network redirector (mapped shares, RDP drive redirection, VM shared folders) are false.
    /// Decided from the drive's DOS-device mapping, not GetDriveType, which can block on a disconnected share.
    /// </summary>
    public static bool IsLocalDrivePath(string path) => IsLocalDrivePath(path, CurrentDriveDevices(refresh: false));

    internal static bool IsLocalDrivePath(string path, IReadOnlyList<(string Drive, string Device)> drives)
    {
        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
        {
            return false;
        }

        return IsLocalDrive(p[..2], drives, depth: 0);
    }

    private static bool IsLocalDrive(string drive, IReadOnlyList<(string Drive, string Device)> drives, int depth)
    {
        string? device = null;
        foreach ((string letter, string target) in drives)
        {
            if (letter.Equals(drive, StringComparison.OrdinalIgnoreCase))
            {
                device = target;
                break;
            }
        }

        if (device is null)
        {
            return false;
        }

        // SUBST drives map to "\??\C:\folder": local exactly when the drive they point into is.
        if (device.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            string target = device[4..];
            return depth < 4 && target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':'
                && IsLocalDrive(target[..2], drives, depth + 1);
        }

        return device.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase)
            || device.StartsWith(@"\Device\CdRom", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Drive, string Device)> CurrentDriveDevices(bool refresh)
    {
        if (!refresh && DriveDevices.TryGet(string.Empty, out IReadOnlyList<(string Drive, string Device)>? cached))
        {
            return cached;
        }

        IReadOnlyList<(string Drive, string Device)> fresh = SystemQueries.GetDriveDevices();
        DriveDevices.Set(string.Empty, fresh);
        return fresh;
    }
}
