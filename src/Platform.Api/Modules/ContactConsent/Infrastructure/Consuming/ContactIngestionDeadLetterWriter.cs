using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

/// <summary>What the dead-letter record must say about one refused contact declaration.</summary>
internal sealed record ContactIngestionDiagnosis
{
    /// <summary>Member of the published refusal vocabulary of this ingestion.</summary>
    public required string Reason { get; init; }

    /// <summary>Reverse-DNS event type, when the envelope could be read.</summary>
    public string? EventType { get; init; }

    /// <summary>URN of the emitting system, when the envelope could be read.</summary>
    public string? EventSource { get; init; }

    /// <summary>Producer-assigned event id; the correlation the emitting team holds.</summary>
    public string? EventId { get; init; }

    /// <summary>Event body, read only through the allow-list of the summary.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>
/// Records one permanently invalid contact declaration on the dead-letter
/// topic of this ingestion.
///
/// The published body is never the original one. Every record on this topic
/// carries an e-mail address or a phone number in the clear by construction,
/// and the dead-letter topic keeps records fourteen times longer than the
/// entry topic, so copying the body verbatim would move personal data to
/// exactly the place it should not sit. What travels is a summary rebuilt from
/// an allow-list: the event type, the source, how many contact points were
/// declared and the channel of each by position, and the consent entries. The
/// contact value never travels, and neither does its keyed hash, which is
/// deterministic and would hand out a stable correlatable pseudonym that is
/// still personal data.
///
/// The accepted consequence is that this pair of topics has no redrive: the
/// record is not a faithful copy. With declarative semantics the repair is the
/// emitting system publishing the correct state again, idempotent by
/// construction, and the original body is still on the entry topic within its
/// retention window.
/// </summary>
internal sealed class ContactIngestionDeadLetterWriter(
    IKafkaDeadLetterProducer producer,
    IOptions<ContactsIngressOptions> options,
    TimeProvider timeProvider,
    ILogger<ContactIngestionDeadLetterWriter> logger)
{
    internal const string EventSourceHeader = "eventSource";
    internal const string EventIdHeader = "eventId";
    internal const string EventTypeHeader = "eventType";

    private const string ContactPointsProperty = "contactPoints";
    private const string ConsentsProperty = "consents";
    private const string ChannelProperty = "channel";

    public async Task ProduceAsync(
        KafkaMessageContext context,
        ContactIngestionDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnosis);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeadLetterHeaders.Reason] = diagnosis.Reason,
            [DeadLetterHeaders.SourceTopic] = context.Topic,
            [DeadLetterHeaders.SourcePartition] =
                context.Partition.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.SourceOffset] =
                context.Offset.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.OccurredAt] = timeProvider.GetUtcNow().ToString("O"),

            // Unconditional here, unlike the ingestion of notification
            // requests: there is no body on this topic that could be published
            // as it arrived.
            [DeadLetterHeaders.Redacted] = "true",
        };
        AddWhenPresent(headers, EventSourceHeader, diagnosis.EventSource);
        AddWhenPresent(headers, EventIdHeader, diagnosis.EventId);
        AddWhenPresent(headers, EventTypeHeader, diagnosis.EventType);
        AddWhenPresent(
            headers,
            DeadLetterHeaders.Traceparent,
            context.Event?.Traceparent
                ?? (context.Headers.TryGetValue(DeadLetterHeaders.Traceparent, out var traceparent)
                    ? traceparent
                    : null));

        await producer.ProduceAsync(
            new DeadLetterRecord
            {
                Topic = options.Value.DeadLetterTopic,
                Key = context.Key,
                Body = Summarize(diagnosis),
                Headers = headers,
            },
            cancellationToken);

        // After the produce, never before: the line claims a record exists.
        logger.ContactDeadLetterProduced(
            options.Value.DeadLetterTopic,
            diagnosis.Reason,
            context.Topic,
            context.Partition,
            context.Offset,
            diagnosis.EventSource,
            diagnosis.EventType);
    }

    /// <summary>
    /// Rebuilds the diagnosable shape of one refused declaration from an
    /// allow-list of fields. Nothing is copied from the original body: each
    /// field is read by name and only when it holds the expected JSON kind, so
    /// a value the allow-list does not name cannot travel by accident.
    /// </summary>
    internal static string Summarize(ContactIngestionDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        var summary = new JsonObject
        {
            ["reason"] = diagnosis.Reason,
            ["eventType"] = diagnosis.EventType,
            ["eventSource"] = diagnosis.EventSource,
            ["eventId"] = diagnosis.EventId,
        };

        if (diagnosis.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return summary.ToJsonString();
        }

        if (data.TryGetProperty(ContactPointsProperty, out JsonElement contactPoints)
            && contactPoints.ValueKind == JsonValueKind.Array)
        {
            var channels = new JsonArray();
            foreach (JsonElement point in contactPoints.EnumerateArray())
            {
                channels.Add(JsonValue.Create(ReadString(point, ChannelProperty)));
            }

            summary["contactPointCount"] = channels.Count;
            summary["contactPointChannels"] = channels;
        }

        if (data.TryGetProperty(ConsentsProperty, out JsonElement consents)
            && consents.ValueKind == JsonValueKind.Array)
        {
            var entries = new JsonArray();
            foreach (JsonElement consent in consents.EnumerateArray())
            {
                entries.Add(new JsonObject
                {
                    ["purpose"] = ReadString(consent, "purpose"),
                    ["channel"] = ReadString(consent, ChannelProperty),
                    ["granted"] = ReadBoolean(consent, "granted"),
                    ["source"] = ReadString(consent, "source"),
                    ["termsVersion"] = ReadString(consent, "termsVersion"),
                });
            }

            summary[ConsentsProperty] = entries;
        }

        return summary.ToJsonString();
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool? ReadBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static void AddWhenPresent(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            headers[name] = value;
        }
    }
}
