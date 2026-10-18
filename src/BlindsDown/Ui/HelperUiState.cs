namespace BlindsDown.Ui;

internal enum HelperAvailability
{
    /// <summary>Not installed: cameras can't be locked. Settings offers "Turn on camera lock".</summary>
    NotInstalled,

    /// <summary>Installed and answering.</summary>
    Working,

    /// <summary>Installed, but from a different BlindsDown version: Settings offers "Update helper" (or "Replace
    /// helper" when the helper is the newer one; see <see cref="Forms.HelperText.CompareVersions"/>).</summary>
    NeedsUpdate,

    /// <summary>Installed but not answering (stopped, starting, or broken).</summary>
    NotResponding,

    /// <summary>An install, update or removal is running right now.</summary>
    Busy,
}

/// <summary>What Settings shows about the optional camera helper.</summary>
/// <param name="Availability">Its state.</param>
/// <param name="HelperVersion">The installed helper's version, if known.</param>
/// <param name="Detail">One sentence for the user, e.g. the last error ("Windows didn't allow the install.").</param>
internal sealed record HelperUiState(HelperAvailability Availability, string? HelperVersion = null, string? Detail = null)
{
    public static HelperUiState NotInstalled { get; } = new(HelperAvailability.NotInstalled);
}
