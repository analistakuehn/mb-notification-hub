using System.Text.Json;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// One CloudEvents 1.0 structured-mode event as it travels on the corporate
/// bus. The bus envelope is deliberately not the internal queue envelope:
/// integration partners speak CloudEvents, the internal queues speak the claim
/// check, and collapsing the two would leak one contract into the other.
/// </summary>
public sealed record CloudEvent
{
    /// <summary>Producer-assigned event id; a legitimate resend carries a new one.</summary>
    public required string Id { get; init; }

    /// <summary>URN of the emitting system.</summary>
    public required string Source { get; init; }

    /// <summary>Reverse-DNS event type, versioned in its own name.</summary>
    public required string Type { get; init; }

    /// <summary>Subject the event is about; the recipient id on this bus.</summary>
    public string? Subject { get; init; }

    public DateTimeOffset? Time { get; init; }

    /// <summary>W3C tracing context carried as a CloudEvents extension attribute.</summary>
    public string? Traceparent { get; init; }

    /// <summary>Event body; the domain payload agreed with the producer.</summary>
    public required JsonElement Data { get; init; }
}

/// <summary>
/// Outcome of parsing one bus record into a CloudEvent: either the event or
/// the stable reason the record is permanently invalid. A parse failure is
/// permanent by contract: redelivery can never fix a malformed body.
/// </summary>
public sealed record CloudEventParse
{
    private CloudEventParse()
    {
    }

    public CloudEvent? Event { get; private init; }

    /// <summary>Stable reason when the record is permanently invalid.</summary>
    public string? InvalidReason { get; private init; }

    public static CloudEventParse Valid(CloudEvent cloudEvent) => new() { Event = cloudEvent };

    public static CloudEventParse Invalid(string reason) => new() { InvalidReason = reason };
}

/// <summary>Parses the structured-mode CloudEvents shape out of a raw bus record body.</summary>
public static class CloudEventParser
{
    public const string SpecVersion = "1.0";

    public const string ReasonMalformedJson = "cloudevent-malformed-json";
    public const string ReasonNotAnObject = "cloudevent-not-an-object";
    public const string ReasonUnsupportedSpecVersion = "cloudevent-unsupported-specversion";
    public const string ReasonMissingId = "cloudevent-missing-id";
    public const string ReasonMissingSource = "cloudevent-missing-source";
    public const string ReasonMissingType = "cloudevent-missing-type";
    public const string ReasonMissingData = "cloudevent-missing-data";

    public static CloudEventParse Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return CloudEventParse.Invalid(ReasonMalformedJson);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CloudEventParse.Invalid(ReasonNotAnObject);
            }

            if (ReadString(root, "specversion") is not { } specVersion
                || !string.Equals(specVersion, SpecVersion, StringComparison.Ordinal))
            {
                return CloudEventParse.Invalid(ReasonUnsupportedSpecVersion);
            }

            if (ReadString(root, "id") is not { Length: > 0 } id)
            {
                return CloudEventParse.Invalid(ReasonMissingId);
            }

            if (ReadString(root, "source") is not { Length: > 0 } source)
            {
                return CloudEventParse.Invalid(ReasonMissingSource);
            }

            if (ReadString(root, "type") is not { Length: > 0 } type)
            {
                return CloudEventParse.Invalid(ReasonMissingType);
            }

            if (!root.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return CloudEventParse.Invalid(ReasonMissingData);
            }

            DateTimeOffset? time = null;
            if (root.TryGetProperty("time", out JsonElement timeElement)
                && timeElement.ValueKind == JsonValueKind.String
                && timeElement.TryGetDateTimeOffset(out DateTimeOffset parsedTime))
            {
                time = parsedTime;
            }

            return CloudEventParse.Valid(new CloudEvent
            {
                Id = id,
                Source = source,
                Type = type,
                Subject = ReadString(root, "subject"),
                Time = time,
                Traceparent = ReadString(root, "traceparent"),
                Data = data.Clone(),
            });
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
