using System.Reflection;

namespace BlindsDown;

internal static class AppVersion
{
    /// <summary>The version from the build (e.g. "0.1.0"), without the source-control suffix.</summary>
    public static string Text { get; } = Read();

    private static string Read()
    {
        string? informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
