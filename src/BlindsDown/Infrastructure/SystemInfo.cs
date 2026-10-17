namespace BlindsDown.Infrastructure;

/// <summary>Small facts about the running system.</summary>
internal static class SystemInfo
{
    /// <summary>
    /// When Windows last booted (UTC), rounded to the second so repeated calls agree.
    /// Computed as now minus the tick count, which keeps counting through sleep and hibernation. With Fast
    /// Startup, "Shut down" hibernates the kernel instead of ending it, so this is the last full kernel
    /// boot (a restart), not the last time the PC was switched on.
    /// </summary>
    public static DateTimeOffset BootTimeUtc() => BootTimeUtc(DateTimeOffset.UtcNow, Environment.TickCount64);

    /// <summary>True in a Remote Desktop session (microphones are then the remote client's).</summary>
    public static bool IsRemoteSession() => SystemInformation.TerminalServerSession;

    internal static DateTimeOffset BootTimeUtc(DateTimeOffset utcNow, long tickCount64Ms)
    {
        long bootTicks = utcNow.UtcTicks - (tickCount64Ms * TimeSpan.TicksPerMillisecond);
        long rounded = (bootTicks + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }
}
