using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BlindsDown.Core.Json;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;

namespace BlindsDown.Core.Helper;

/// <summary>
/// Strict validation and (de)serialization of protocol messages. Every message is checked in full before it is
/// used: the helper runs as LocalSystem and any program on the PC can write to its pipe, and the tray app must not
/// trip over a damaged answer. Parsing never throws; anything malformed returns null with a short reason.
/// </summary>
public static class HelperMessages
{
    /// <summary>The most entries any list in a response may have (devices, problems, events, report lines).</summary>
    internal const int MaxListItems = 256;

    /// <summary>
    /// Parses and validates a request: size, JSON shape (no unknown, repeated or null properties), version,
    /// command, lock options for a lock, exclusion and keep-on counts, string lengths, no control characters
    /// (they would only serve to forge lines in the helper's log). Returns null with an error message for
    /// anything malformed.
    /// </summary>
    public static HelperRequest? ParseRequest(ReadOnlySpan<byte> utf8Json, out string? error)
    {
        HelperRequest? request = Read(utf8Json, "request", HelperRequestJsonContext.Default.HelperRequest, out error);
        if (request is null)
        {
            return null;
        }

        error = CheckRequest(request);
        if (error is not null)
        {
            return null;
        }

        // Run IDs are positive: any other number says as much as none (like SinceEvent, it only shapes the client's own
        // answer, so it is not refused).
        if (request.SinceRun <= 0)
        {
            request = request with { SinceRun = null };
        }

        // The source generator sets an init-only property that is missing from the JSON to null, not to its
        // initializer, so a lock request without exclusions arrives with a null list.
        return request.Lock is null
            ? request
            : request with { Lock = request.Lock with { Exclusions = request.Lock.Exclusions ?? [], KeepEnabledInstanceIds = request.Lock.KeepEnabledInstanceIds ?? [] } };
    }

    public static byte[] SerializeRequest(HelperRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, HelperRequestJsonContext.Default.HelperRequest);

    /// <summary>
    /// Parses and validates a response. Unknown properties are skipped (a newer helper may send more), but the
    /// size, version, enum values, list counts and string lengths are checked, so the UI never meets a null or a
    /// value it can't show.
    /// </summary>
    public static HelperResponse? ParseResponse(ReadOnlySpan<byte> utf8Json, out string? error)
    {
        HelperResponse? response = Read(utf8Json, "response", HelperResponseJsonContext.Default.HelperResponse, out error);
        if (response is null)
        {
            return null;
        }

        // Missing init-only properties arrive as null (see ParseRequest); an explicit null is refused while parsing. A run
        // ID below 0 is as good as none (0), so the client never sends it back.
        response = response with
        {
            HelperVersion = response.HelperVersion ?? string.Empty,
            Events = response.Events ?? [],
            Changes = response.Changes ?? [],
            RunId = Math.Max(0, response.RunId),
        };
        error = CheckResponse(response);
        return error is null ? response : null;
    }

    public static byte[] SerializeResponse(HelperResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, HelperResponseJsonContext.Default.HelperResponse);

    /// <summary>
    /// The response trimmed so that <see cref="ParseResponse"/> accepts it: no null strings or entries, every
    /// string within <see cref="HelperProtocol.MaxStringLength"/>, every list within <see cref="MaxListItems"/>
    /// (the newest events are kept). A device name or a driver's error text can be longer than the protocol
    /// allows, and one long string must not make the tray app throw the whole answer away.
    /// </summary>
    internal static HelperResponse ForWire(HelperResponse response) => response with
    {
        Error = response.Error is null ? null : Clip(response.Error),
        HelperVersion = Clip(response.HelperVersion),
        Status = response.Status is null ? null : ForWire(response.Status),
        Events = [.. (response.Events ?? []).OfType<HelperEvent>().TakeLast(MaxListItems).Select(e => e with { DeviceName = Clip(e.DeviceName), Message = Clip(e.Message) })],
        Changes =
        [
            .. (response.Changes ?? []).OfType<HelperChange>().TakeLast(MaxListItems).Select(c => c with
            {
                RequestDigest = c.RequestDigest is null ? null : Clip(c.RequestDigest),
                Source = c.Source is null ? null : Clip(c.Source),
                TurnedOn = [.. (c.TurnedOn ?? []).OfType<string>().Take(MaxListItems).Select(n => Clip(n))],
                Released = [.. (c.Released ?? []).OfType<string>().Take(MaxListItems).Select(n => Clip(n))],
            }),
        ],
        Report = response.Report is null ? null : response.Report with
        {
            Lines = [.. (response.Report.Lines ?? []).OfType<RestoreLine>().Take(MaxListItems).Select(l => l with { DeviceName = Clip(l.DeviceName), Message = Clip(l.Message) })],
        },
    };

    /// <summary>Shortens <paramref name="text"/> to <paramref name="max"/> characters (never splitting a surrogate pair).</summary>
    internal static string Clip(string? text, int max = HelperProtocol.MaxStringLength)
    {
        if (text is null)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        int cut = max - 3;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "...";
    }

    private static DeviceLockStatus ForWire(DeviceLockStatus status) => status with
    {
        Devices = [.. (status.Devices ?? []).OfType<DeviceLockInfo>().Take(MaxListItems).Select(d => d with
        {
            Id = Clip(d.Id),
            Name = Clip(d.Name),
            StableKey = Clip(d.StableKey),
            Problem = d.Problem is null ? null : Clip(d.Problem),
        })],
        Problems = [.. (status.Problems ?? []).OfType<string>().Take(MaxListItems).Select(p => Clip(p))],
    };

    private static T? Read<T>(ReadOnlySpan<byte> utf8Json, string what, JsonTypeInfo<T> type, out string? error)
        where T : class
    {
        if (utf8Json.IsEmpty)
        {
            error = $"The {what} is empty.";
            return null;
        }

        if (utf8Json.Length > HelperProtocol.MaxMessageBytes)
        {
            error = $"The {what} is larger than {HelperProtocol.MaxMessageBytes} bytes.";
            return null;
        }

        // The version first, so a message from an older or newer BlindsDown is reported as exactly that, not as
        // "unknown property" from whatever its version added.
        if (PeekVersion(utf8Json) is int version && version != HelperProtocol.Version)
        {
            error = VersionError(version);
            return null;
        }

        try
        {
            T? value = JsonSerializer.Deserialize(utf8Json, type);
            error = value is null ? $"The {what} is empty." : null;
            return value;
        }
        catch (Exception ex)
        {
            // Never throws: malformed input of any kind (bad JSON, bad UTF-8, wrong types) is simply refused.
            error = Clip($"The {what} is not valid: {ex.Message}", 200);
            return null;
        }
    }

    /// <summary>The top-level "v" property, or null when there is none or the text isn't a JSON object.</summary>
    private static int? PeekVersion(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = HelperJsonLimits.MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isVersion = reader.ValueTextEquals("v"u8);
                if (!reader.Read())
                {
                    return null;
                }

                if (isVersion)
                {
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int version) ? version : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // The full parse reports what is wrong.
        }

        return null;
    }

    private static string VersionError(int version) =>
        $"Protocol version {version} is not supported (this BlindsDown speaks version {HelperProtocol.Version}).";

    private static string? CheckRequest(HelperRequest request)
    {
        if (request.V != HelperProtocol.Version)
        {
            return VersionError(request.V);
        }

        if (!Enum.IsDefined(request.Command))
        {
            return "Unknown command.";
        }

        if (request.Command == HelperCommand.Lock && request.Lock is null)
        {
            return "A lock request needs lock options.";
        }

        return CheckText(request.ClientVersion, "The client version")
            ?? CheckText(request.Source, "The source")
            ?? CheckRequestId(request.RequestId)
            ?? (request.Lock is null ? null : CheckLockOptions(request.Lock));
    }

    private static string? CheckRequestId(string? id) =>
        id is null || (id.Length is > 0 and <= HelperProtocol.MaxRequestIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            ? null
            : $"The request ID must be 1 to {HelperProtocol.MaxRequestIdLength} letters, digits or '-'.";

    private static string? CheckLockOptions(DeviceLockRequest options)
    {
        IReadOnlyList<DeviceExclusion> exclusions = options.Exclusions ?? [];
        IReadOnlyList<string> keepOn = options.KeepEnabledInstanceIds ?? [];
        if (exclusions.Count > HelperProtocol.MaxExclusions)
        {
            return $"Too many exclusions (at most {HelperProtocol.MaxExclusions}).";
        }

        if (keepOn.Count > HelperProtocol.MaxExclusions)
        {
            return $"Too many devices to keep on (at most {HelperProtocol.MaxExclusions}).";
        }

        foreach (DeviceExclusion? exclusion in exclusions)
        {
            if (exclusion is null)
            {
                return "An exclusion is empty.";
            }

            string? problem = CheckText(exclusion.StableKey, "An exclusion's key")
                ?? CheckText(exclusion.LastKnownId, "An exclusion's device ID")
                ?? CheckText(exclusion.DisplayName, "An exclusion's name");
            if (problem is not null)
            {
                return problem;
            }
        }

        foreach (string? id in keepOn)
        {
            string? problem = id is null ? "A device to keep on is empty." : CheckText(id, "A device to keep on");
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckText(string? value, string what)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > HelperProtocol.MaxStringLength)
        {
            return $"{what} is longer than {HelperProtocol.MaxStringLength} characters.";
        }

        return value.Any(char.IsControl) ? $"{what} contains control characters." : null;
    }

    private static string? CheckResponse(HelperResponse response)
    {
        if (response.V != HelperProtocol.Version)
        {
            return VersionError(response.V);
        }

        return CheckLength(response.Error, "The error")
            ?? CheckLength(response.HelperVersion, "The helper version")
            ?? (response.Status is null ? null : CheckStatus(response.Status))
            ?? CheckEvents(response.Events)
            ?? CheckChanges(response.Changes)
            ?? (response.Report is null ? null : CheckReport(response.Report));
    }

    private static string? CheckChanges(IReadOnlyList<HelperChange> changes)
    {
        if (changes.Count > MaxListItems)
        {
            return "The response has too many changes.";
        }

        foreach (HelperChange? change in changes)
        {
            if (change is null || change.TurnedOn is null)
            {
                return "A change is empty.";
            }

            if (!Enum.IsDefined(change.Kind) || change.TurnedOn.Count > MaxListItems)
            {
                return "A change has an unknown kind or too many devices.";
            }

            if (change.Released is null || change.Released.Count > MaxListItems)
            {
                return "A change has too many devices.";
            }

            string? problem = CheckLength(change.RequestDigest, "A change's request fingerprint")
                ?? CheckLength(change.Source, "A change's source")
                ?? change.TurnedOn.Concat(change.Released).Select(name => name is null ? "A change lists an empty device." : CheckLength(name, "A change's device name")).FirstOrDefault(p => p is not null);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckStatus(DeviceLockStatus status)
    {
        if (!Enum.IsDefined(status.State))
        {
            return "The lock state is unknown.";
        }

        if (status.Devices.Count > MaxListItems || status.Problems.Count > MaxListItems)
        {
            return "The status lists too many devices or problems.";
        }

        foreach (DeviceLockInfo? device in status.Devices)
        {
            if (device is null)
            {
                return "A device in the status is empty.";
            }

            if (!Enum.IsDefined(device.Kind) || !Enum.IsDefined(device.Protection) || (device.Camera is { } camera && !Enum.IsDefined(camera)))
            {
                return "A device in the status has an unknown kind or protection.";
            }

            string? problem = CheckLength(device.Id, "A device ID")
                ?? CheckLength(device.Name, "A device name")
                ?? CheckLength(device.StableKey, "A device key")
                ?? CheckLength(device.Problem, "A device problem");
            if (problem is not null)
            {
                return problem;
            }
        }

        foreach (string? problem in status.Problems)
        {
            string? error = problem is null ? "A problem in the status is empty." : CheckLength(problem, "A problem");
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? CheckEvents(IReadOnlyList<HelperEvent> events)
    {
        if (events.Count > MaxListItems)
        {
            return "The response has too many events.";
        }

        foreach (HelperEvent? e in events)
        {
            if (e is null)
            {
                return "An event is empty.";
            }

            if (!Enum.IsDefined(e.Kind))
            {
                return "An event has an unknown kind.";
            }

            string? problem = CheckLength(e.DeviceName, "An event's device name") ?? CheckLength(e.Message, "An event's message");
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckReport(RestoreReport report)
    {
        if (report.DeviceCount < 0 || report.Lines.Count > MaxListItems)
        {
            return "The restore report has an impossible size.";
        }

        foreach (RestoreLine? line in report.Lines)
        {
            if (line is null)
            {
                return "A restore report line is empty.";
            }

            if (!Enum.IsDefined(line.Outcome) || !Enum.IsDefined(line.Kind))
            {
                return "A restore report line has an unknown outcome or kind.";
            }

            string? problem = CheckLength(line.DeviceName, "A restore report device name") ?? CheckLength(line.Message, "A restore report message");
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckLength(string? value, string what) =>
        value is not null && value.Length > HelperProtocol.MaxStringLength
            ? $"{what} is longer than {HelperProtocol.MaxStringLength} characters."
            : null;
}
