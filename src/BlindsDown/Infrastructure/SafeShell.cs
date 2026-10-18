using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BlindsDown.Core.Usage;
using BlindsDown.Usage;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Infrastructure;

/// <summary>
/// The ONLY place BlindsDown may start another program. It can open a fixed set of Windows
/// Settings pages (ms-settings: URIs), and start ITSELF with administrator rights with one of three fixed
/// options (installing, updating or removing the camera helper), optionally followed by " --request-id " and
/// 32 lowercase hex characters (the ID the setup run sends with its unlock requests); nothing else: no URLs, no
/// arbitrary paths, no other arguments. Callers can't pass a string in for the Settings pages; every target is a
/// constant below, and it is re-checked against the allowlist right before launching so a future edit can't widen
/// it by accident.
/// </summary>
internal static partial class SafeShell
{
    internal const string MicrophonePrivacyUri = "ms-settings:privacy-microphone";
    internal const string CameraPrivacyUri = "ms-settings:privacy-webcam";
    internal const string SoundUri = "ms-settings:sound";
    internal const string TaskbarUri = "ms-settings:taskbar";
    internal const string StartupAppsUri = "ms-settings:startupapps";

    /// <summary>The options BlindsDown may start itself with elevated (see Helper.ElevatedLauncher).</summary>
    internal const string InstallHelperArguments = "--install-helper";
    internal const string UpdateHelperArguments = "--update-helper";
    internal const string UninstallHelperArguments = "--uninstall-helper";

    /// <summary>Everything BlindsDown can ever open. Matched exactly (ordinal, case-sensitive).</summary>
    private static readonly FrozenSet<string> Allowlist =
        new[] { MicrophonePrivacyUri, CameraPrivacyUri, SoundUri, TaskbarUri, StartupAppsUri }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every argument string without a request ID BlindsDown may start itself elevated with. Matched exactly.</summary>
    private static readonly FrozenSet<string> ElevatedArgumentsAllowlist =
        new[] { InstallHelperArguments, UpdateHelperArguments, UninstallHelperArguments }.ToFrozenSet(StringComparer.Ordinal);

    public static void OpenPrivacySettings(CaptureKind kind) => Open(PrivacySettingsUri(kind));

    public static void OpenSoundSettings() => Open(SoundUri);

    /// <summary>Settings > Personalization > Taskbar (where tray icons are pinned).</summary>
    public static void OpenTaskbarSettings() => Open(TaskbarUri);

    /// <summary>Settings > Apps > Startup (the same switches as Task Manager's "Startup apps").</summary>
    public static void OpenStartupApps() => Open(StartupAppsUri);

    internal static string PrivacySettingsUri(CaptureKind kind) => kind == CaptureKind.Camera ? CameraPrivacyUri : MicrophonePrivacyUri;

    /// <summary>True only for the exact allowlisted URIs (no prefixes, no case or whitespace variants).</summary>
    internal static bool IsAllowed(string? uri) => uri is not null && Allowlist.Contains(uri);

    /// <summary>
    /// True only for the three exact helper options, alone or followed by exactly " --request-id " and 32 lowercase
    /// hex characters (one space each, no quotes, no other arguments, no case or whitespace variants).
    /// </summary>
    internal static bool IsAllowedElevatedArguments(string? arguments) =>
        arguments is not null && (ElevatedArgumentsAllowlist.Contains(arguments) || ElevatedArgumentsWithRequestId().IsMatch(arguments));

    /// <summary>
    /// One of the three options, one space, "--request-id", one space, 32 characters 0-9 or a-f, and the end of the
    /// string (\A and \z: no line breaks or anything else before or after). Case-sensitive on purpose (no
    /// IgnoreCase): the only IDs ever passed are <c>Guid.ToString("N")</c>, which is lowercase. Nothing in it can
    /// carry a quote, a space or a path, so the ID can't add an argument of its own.
    /// </summary>
    [GeneratedRegex(@"\A(?:--install-helper|--update-helper|--uninstall-helper) --request-id [0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ElevatedArgumentsWithRequestId();

    /// <summary>
    /// Starts THIS program (<see cref="Environment.ProcessPath"/>, never another file) with administrator rights and
    /// one of the fixed helper options (optionally with a request ID, see <see cref="IsAllowedElevatedArguments"/>),
    /// so Windows shows its permission (UAC) prompt. Returns the new process, or null when the user said No at the
    /// prompt. Blocks while the prompt is shown: call it off the UI thread.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="arguments"/> is not one of the allowed forms; nothing was started.</exception>
    /// <exception cref="InvalidOperationException">This program's own file is unknown or not on a local disk.</exception>
    /// <exception cref="Win32Exception">Windows could not start it.</exception>
    internal static unsafe SafeProcessHandle? StartSelfElevated(string arguments, nint ownerWindow)
    {
        if (!IsAllowedElevatedArguments(arguments))
        {
            Log.Error($"SafeShell refused to start BlindsDown elevated with options that are not on its allowlist: '{arguments}'");
            throw new ArgumentException("Not an allowed option for starting BlindsDown with administrator rights.", nameof(arguments));
        }

        string exe = OwnExecutable()
            ?? throw new InvalidOperationException("BlindsDown can only ask for administrator rights when its own BlindsDown.exe runs from a local disk.");

        fixed (char* verb = "runas")
        fixed (char* file = exe)
        fixed (char* parameters = arguments)
        {
            var info = new SafeShellNative.ShellExecuteInfo
            {
                Size = (uint)sizeof(SafeShellNative.ShellExecuteInfo),
                Mask = SafeShellNative.SeeMaskNoCloseProcess | SafeShellNative.SeeMaskNoAsync,
                Window = ownerWindow,
                Verb = verb,
                File = file,
                Parameters = parameters,
                Show = SafeShellNative.SwShowNormal,
            };

            if (SafeShellNative.ShellExecuteExW(&info) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == SafeShellNative.ErrorCancelled)
                {
                    return null;
                }

                throw new Win32Exception(error);
            }

            return info.Process == 0
                ? throw new Win32Exception(SafeShellNative.ErrorInvalidHandle, "Windows started BlindsDown but did not return the process.")
                : new SafeProcessHandle(info.Process, ownsHandle: true);
        }
    }

    /// <summary>
    /// This program's own exe (<see cref="Environment.ProcessPath"/>), or null when that isn't BlindsDown's exe on a
    /// local disk. Started as "dotnet BlindsDown.dll", the process is dotnet.exe; loaded by a test runner, it is the
    /// runner's exe. Neither may ever be started elevated or installed in BlindsDown's place. And an elevated start
    /// or a copy reads the file again, which from a network share would be network traffic.
    /// </summary>
    internal static string? OwnExecutable()
    {
        string? exe = Environment.ProcessPath;
        return exe is not null
            && IsBlindsDownTheProgram()
            && IsAppExecutable(exe, AppContext.BaseDirectory)
            && ImagePaths.IsLocalDrivePath(exe) ? exe : null;
    }

    /// <summary>True when BlindsDown's own code is the program that runs (its entry point), not a library that another
    /// program in the same folder loaded.</summary>
    internal static bool IsBlindsDownTheProgram() =>
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, typeof(SafeShell).Assembly.GetName().Name, StringComparison.Ordinal);

    /// <summary>True when <paramref name="exe"/> is a full path to an .exe directly in the app's folder (where both a
    /// single-file build and a development build's BlindsDown.exe live).</summary>
    internal static bool IsAppExecutable(string exe, string appDirectory)
    {
        if (!Path.IsPathFullyQualified(exe) || !Path.IsPathFullyQualified(appDirectory)
            || !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? folder = Path.GetDirectoryName(Path.GetFullPath(exe));
        return folder is not null
            && string.Equals(folder.TrimEnd('\\'), Path.GetFullPath(appDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static void Open(string uri)
    {
        if (!IsAllowed(uri))
        {
            Log.Error($"SafeShell refused to open a target that is not on its allowlist: '{uri}'");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            };

#pragma warning disable RS0030 // The single audited process start: uri was just checked against the fixed allowlist of ms-settings: pages, so it can never be a URL, path or user input.
            Process.Start(startInfo)?.Dispose();
#pragma warning restore RS0030
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // E.g. Settings is disabled by policy. Opening Settings is a convenience; never fail the caller.
            Log.Warn($"Could not open {uri}: {ex.Message}");
        }
    }

    /// <summary>ShellExecuteExW lives here, inside SafeShell, so no other code can call it.</summary>
    private static unsafe partial class SafeShellNative
    {
        public const uint SeeMaskNoCloseProcess = 0x00000040;

        /// <summary>Finish the launch before returning (the calling thread may end right after).</summary>
        public const uint SeeMaskNoAsync = 0x00000100;

        public const int SwShowNormal = 1;

        /// <summary>The user said No at the permission prompt.</summary>
        public const int ErrorCancelled = 1223;

        public const int ErrorInvalidHandle = 6;

        [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
        public static partial int ShellExecuteExW(ShellExecuteInfo* info);

        /// <summary>SHELLEXECUTEINFOW.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ShellExecuteInfo
        {
            public uint Size;
            public uint Mask;
            public nint Window;
            public char* Verb;
            public char* File;
            public char* Parameters;
            public char* Directory;
            public int Show;
            public nint InstApp;
            public void* IdList;
            public char* Class;
            public nint ClassKey;
            public uint HotKey;
            public nint IconOrMonitor;
            public nint Process;
        }
    }
}
