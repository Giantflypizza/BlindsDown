using BlindsDown.Core.Helper;
using Microsoft.Win32;

namespace BlindsDown.Helper;

/// <summary>
/// The helper's entry in Settings > Apps > Installed apps (and the classic "Programs and Features"), so it can be
/// found and removed like any other program. Removing it there runs "--uninstall-helper", which asks Windows for
/// administrator rights itself.
/// </summary>
internal sealed record UninstallEntry(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string DisplayIcon,
    string UninstallString,
    string QuietUninstallString,
    string InstallLocation,
    int EstimatedSizeKb)
{
    /// <summary>Under HKEY_LOCAL_MACHINE (64-bit view).</summary>
    internal const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + HelperProtocol.ServiceName;

    public static UninstallEntry For(string installedExe, string version, long totalBytes)
    {
        string command = $"\"{installedExe}\" {ElevatedLauncher.Arguments(ElevatedTask.UninstallHelper)}";
        return new UninstallEntry(
            HelperProtocol.ServiceDisplayName,
            version,
            "BlindsDown",
            installedExe + ",0",
            command,
            command,
            Path.GetDirectoryName(installedExe) ?? installedExe,
            (int)Math.Clamp((totalBytes + 1023) / 1024, 0, int.MaxValue));
    }

    /// <summary>The registry values, strings first. "NoModify" and "NoRepair": there is nothing to change or repair,
    /// only to remove.</summary>
    public IReadOnlyList<(string Name, object Value, RegistryValueKind Kind)> Values() =>
    [
        ("DisplayName", DisplayName, RegistryValueKind.String),
        ("DisplayVersion", DisplayVersion, RegistryValueKind.String),
        ("Publisher", Publisher, RegistryValueKind.String),
        ("DisplayIcon", DisplayIcon, RegistryValueKind.String),
        ("UninstallString", UninstallString, RegistryValueKind.String),
        ("QuietUninstallString", QuietUninstallString, RegistryValueKind.String),
        ("InstallLocation", InstallLocation, RegistryValueKind.String),
        ("NoModify", 1, RegistryValueKind.DWord),
        ("NoRepair", 1, RegistryValueKind.DWord),
        ("EstimatedSize", EstimatedSizeKb, RegistryValueKind.DWord),
    ];

    public static void Delete()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        machine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>Needs administrator rights.</summary>
    public void Write()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey key = machine.CreateSubKey(KeyPath, writable: true);
        foreach ((string name, object value, RegistryValueKind kind) in Values())
        {
            key.SetValue(name, value, kind);
        }
    }
}
