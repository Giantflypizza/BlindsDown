using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using BlindsDown.Core.Usage;

namespace BlindsDown.Infrastructure;

/// <summary>
/// The ONLY place BlindsDown may start another program. It can open a fixed set of Windows
/// Settings pages (ms-settings: URIs) and nothing else: no URLs, no arbitrary paths.
/// Callers can't pass a string in; every target is a constant below, and it is re-checked against
/// the allowlist right before launching so a future edit can't widen it by accident.
/// </summary>
internal static class SafeShell
{
    internal const string MicrophonePrivacyUri = "ms-settings:privacy-microphone";
    internal const string CameraPrivacyUri = "ms-settings:privacy-webcam";
    internal const string SoundUri = "ms-settings:sound";
    internal const string TaskbarUri = "ms-settings:taskbar";
    internal const string StartupAppsUri = "ms-settings:startupapps";

    /// <summary>Everything BlindsDown can ever open. Matched exactly (ordinal, case-sensitive).</summary>
    private static readonly FrozenSet<string> Allowlist =
        new[] { MicrophonePrivacyUri, CameraPrivacyUri, SoundUri, TaskbarUri, StartupAppsUri }.ToFrozenSet(StringComparer.Ordinal);

    public static void OpenPrivacySettings(CaptureKind kind) => Open(PrivacySettingsUri(kind));

    public static void OpenSoundSettings() => Open(SoundUri);

    /// <summary>Settings > Personalization > Taskbar (where tray icons are pinned).</summary>
    public static void OpenTaskbarSettings() => Open(TaskbarUri);

    /// <summary>Settings > Apps > Startup (the same switches as Task Manager's "Startup apps").</summary>
    public static void OpenStartupApps() => Open(StartupAppsUri);

    internal static string PrivacySettingsUri(CaptureKind kind) => kind == CaptureKind.Camera ? CameraPrivacyUri : MicrophonePrivacyUri;

    /// <summary>True only for the exact allowlisted URIs (no prefixes, no case or whitespace variants).</summary>
    internal static bool IsAllowed(string? uri) => uri is not null && Allowlist.Contains(uri);

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
}
