using System.Text.Json.Serialization;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Usage;

namespace BlindsDown.Core.Json;

/// <summary>Compile-time generated JSON (de)serializers for BlindsDown's files (no reflection).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(LockJournal))]
[JsonSerializable(typeof(List<LockAction>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}

/// <summary>Single-line JSON for history.jsonl and lock-events.jsonl.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UsageRecord))]
[JsonSerializable(typeof(LockEventRecord))]
internal sealed partial class CoreJsonLineContext : JsonSerializerContext
{
}
