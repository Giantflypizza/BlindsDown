using System.Text.Json.Serialization;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Core.Json;

/// <summary>
/// Requests to the helper (compile-time generated, no reflection). Read strictly, because the helper runs as
/// LocalSystem and any program on the PC can send it bytes: unknown or repeated properties, nulls where the
/// model has none, missing required values and deep nesting are all refused before a request reaches the
/// engine. <see cref="HelperMessages"/> checks the rest (version, enum values, counts, lengths).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = HelperJsonLimits.MaxDepth)]
[JsonSerializable(typeof(HelperRequest))]
internal sealed partial class HelperRequestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Responses from the helper. Unknown properties are skipped (a newer helper may add some); everything else is
/// as strict as requests. Nulls are written out, because every parameter of the positional records (such as
/// <see cref="Locking.DeviceLockInfo.Problem"/>) must be present. Computed properties such as
/// <see cref="Locking.RestoreReport.Summary"/> are not sent: the tray app computes them from the data.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    IgnoreReadOnlyProperties = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = HelperJsonLimits.MaxDepth)]
[JsonSerializable(typeof(HelperResponse))]
internal sealed partial class HelperResponseJsonContext : JsonSerializerContext
{
}

/// <summary>The helper's journal file (%ProgramData%\BlindsDown\device-lock-journal.json), readable like the app's own.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DeviceLockJournal))]
internal sealed partial class DeviceJournalJsonContext : JsonSerializerContext
{
}

internal static class HelperJsonLimits
{
    /// <summary>The deepest protocol message is four levels (response, status, devices, device); a little room on top.</summary>
    public const int MaxDepth = 8;
}

/// <summary>
/// The helper's change log file (%ProgramData%\BlindsDown\lock-changes.json): one object with the changes and the last
/// lock request. Read leniently (nulls and missing members are allowed); <see cref="FileHelperChangeStore"/> drops what
/// it can't use.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperChangeLog))]
internal sealed partial class HelperChangeLogJsonContext : JsonSerializerContext
{
}
