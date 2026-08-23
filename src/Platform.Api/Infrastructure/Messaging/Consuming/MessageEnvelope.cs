using System.Text.Json;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// The versioned envelope every internal queue message travels in. Producers
/// write it into the outbox payload; the consumer parses it back before any
/// handler runs. The payload is a claim check: consumers re-read state from
/// the database, so no sensitive content ever rides the queue.
/// </summary>
public sealed record MessageEnvelope
{
    /// <summary>Producer-assigned id; joins the business key in the dedupe mark.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Message type in the platform dot vocabulary.</summary>
    public required string Type { get; init; }

    public required int SchemaVersion { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public string? Traceparent { get; init; }

    public string? PriorityClass { get; init; }

    /// <summary>
    /// Queue that delivered this message. Transport metadata stamped by the
    /// platform consumer after parsing, never part of the body: a processor
    /// that must produce follow-up messages to the same queue reads it here.
    /// </summary>
    public string? SourceQueue { get; init; }

    /// <summary>Claim-check payload; the handler reads the identifiers it needs.</summary>
    public required JsonElement Payload { get; init; }
}

/// <summary>
/// Outcome of parsing one message body into the envelope: either the envelope
/// or the stable reason the message is permanently invalid. A parse failure is
/// a permanent error by contract: redelivery can never fix a malformed body.
/// </summary>
public sealed record MessageEnvelopeParse
{
    private MessageEnvelopeParse()
    {
    }

    public MessageEnvelope? Envelope { get; private init; }

    /// <summary>Stable discard reason when the body is permanently invalid.</summary>
    public string? InvalidReason { get; private init; }

    public static MessageEnvelopeParse Valid(MessageEnvelope envelope) => new() { Envelope = envelope };

    public static MessageEnvelopeParse Invalid(string reason) => new() { InvalidReason = reason };
}

/// <summary>Parses the ratified envelope shape out of a raw SQS message body.</summary>
public static class MessageEnvelopeParser
{
    public const string ReasonMalformedJson = "envelope-malformed-json";
    public const string ReasonNotAnObject = "envelope-not-an-object";
    public const string ReasonMissingMessageId = "envelope-missing-message-id";
    public const string ReasonMissingType = "envelope-missing-type";
    public const string ReasonMissingSchemaVersion = "envelope-missing-schema-version";
    public const string ReasonMissingPayload = "envelope-missing-payload";

    public static MessageEnvelopeParse Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return MessageEnvelopeParse.Invalid(ReasonMalformedJson);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MessageEnvelopeParse.Invalid(ReasonNotAnObject);
            }

            if (!root.TryGetProperty("messageId", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idElement.GetString(), out Guid messageId))
            {
                return MessageEnvelopeParse.Invalid(ReasonMissingMessageId);
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() is not { Length: > 0 } type)
            {
                return MessageEnvelopeParse.Invalid(ReasonMissingType);
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var schemaVersion))
            {
                return MessageEnvelopeParse.Invalid(ReasonMissingSchemaVersion);
            }

            if (!root.TryGetProperty("payload", out JsonElement payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return MessageEnvelopeParse.Invalid(ReasonMissingPayload);
            }

            DateTimeOffset? occurredAt = null;
            if (root.TryGetProperty("occurredAt", out JsonElement occurredAtElement)
                && occurredAtElement.ValueKind == JsonValueKind.String
                && occurredAtElement.TryGetDateTimeOffset(out DateTimeOffset parsedOccurredAt))
            {
                occurredAt = parsedOccurredAt;
            }

            return MessageEnvelopeParse.Valid(new MessageEnvelope
            {
                MessageId = messageId,
                Type = type,
                SchemaVersion = schemaVersion,
                OccurredAt = occurredAt,
                Traceparent = ReadOptionalString(root, "traceparent"),
                PriorityClass = ReadOptionalString(root, "priorityClass"),
                Payload = payload.Clone(),
            });
        }
    }

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
