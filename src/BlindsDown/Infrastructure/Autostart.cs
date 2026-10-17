using System.Security;
using Microsoft.Win32;

namespace BlindsDown.Infrastructure;

/// <summary>
/// "Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (value "BlindsDown",
/// quoted exe path + " --autostart"). Respects (reads, never writes) the Task Manager "Startup apps"
/// enabled/disabled state under Explorer\StartupApproved\Run: that switch belongs to the user.
/// Registry errors are logged, never thrown; read <see cref="IsEnabled()"/> afterwards for the real state.
/// </summary>
internal static class Autostart
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "BlindsDown";
    internal const string AutostartArgument = "--autostart";

    /// <summary>True when the Run entry exists and starts THIS exe. An entry left behind by a copy that was
    /// moved or deleted counts as off, so turning the option on repairs it.</summary>
    public static bool IsEnabled() =>
        Environment.ProcessPath is { } exePath && Guard(() => IsEnabled(Registry.CurrentUser, exePath), fallback: false);

    /// <summary>True when the Run entry exists but the user disabled it in Task Manager.</summary>
    public static bool IsDisabledInTaskManager() =>
        Environment.ProcessPath is { } exePath && Guard(() => IsDisabledInTaskManager(Registry.CurrentUser, exePath), fallback: false);

    public static void SetEnabled(bool enabled)
    {
        if (Environment.ProcessPath is not { } exePath)
        {
            Log.Warn("Autostart: the exe path is unknown; leaving the setting unchanged.");
            return;
        }

        Guard(
            () =>
            {
                SetEnabled(Registry.CurrentUser, enabled, exePath);
                Log.Info(enabled ? "Autostart enabled." : "Autostart disabled.");
                return true;
            },
            fallback: false);
    }

    /// <param name="root">HKCU in the app; a throwaway test key in tests.</param>
    internal static bool IsEnabled(RegistryKey root, string exePath)
    {
        using RegistryKey? run = root.OpenSubKey(RunKeyPath);
        return run?.GetValue(ValueName) is string command && SamePath(ExePathFromCommand(command), exePath);
    }

    internal static bool IsDisabledInTaskManager(RegistryKey root, string exePath)
    {
        if (!IsEnabled(root, exePath))
        {
            return false;
        }

        // A binary value: the first byte is 0x02 when enabled and 0x03 when disabled (Task Manager);
        // other tools write 0x06/0x07. The low bit is the "disabled" flag in all of them.
        using RegistryKey? approved = root.OpenSubKey(StartupApprovedRunKeyPath);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 0x01) != 0;
    }

    internal static void SetEnabled(RegistryKey root, bool enabled, string exePath)
    {
        if (enabled)
        {
            using RegistryKey run = root.CreateSubKey(RunKeyPath, writable: true);
            run.SetValue(ValueName, CommandLineFor(exePath), RegistryValueKind.String);
        }
        else
        {
            using RegistryKey? run = root.OpenSubKey(RunKeyPath, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Quoted, because the path may contain spaces ("C:\Program Files\...").</summary>
    internal static string CommandLineFor(string exePath) => "\"" + exePath + "\" " + AutostartArgument;

    /// <summary>The exe path from a Run command line: the quoted part, else everything up to ".exe".</summary>
    internal static string ExePathFromCommand(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }

        int exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? trimmed[..(exe + 4)] : trimmed;
    }

    private static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool Guard(Func<bool> action, bool fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Autostart registry access failed", ex);
            return fallback;
        }
    }
}
