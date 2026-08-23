using System.Text.Json;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>Everything a module supplies to emit one CloudEvent through the outbox.</summary>
public sealed record CloudEventAppend
{
    /// <summary>Topic the relay publishes to.</summary>
    public required string Destination { get; init; }

    /// <summary>URN of the emitting system.</summary>
    public required string Source { get; init; }

    /// <summary>Reverse-DNS event type, versioned in its own name.</summary>
    public required string Type { get; init; }

    /// <summary>Subject the event is about; also the record key that keeps per-subject order.</summary>
    public required string Subject { get; init; }

    /// <summary>Instant the effect happened, from the caller's clock, never from the wall clock here.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Priority class the relay orders its reads by.</summary>
    public required string PriorityClass { get; init; }

    /// <summary>Event body, already shaped by the owning module.</summary>
    public required JsonElement Data { get; init; }

    /// <summary>W3C tracing context of the effect, when one is active.</summary>
    public string? Traceparent { get; init; }
}

/// <summary>
/// Builds the CloudEvents 1.0 structured-mode envelope the corporate bus
/// expects and hands it back as an outbox row. The envelope belongs to the
/// platform because it is transport contract, not domain: modules decide what
/// happened and what the body says, the platform decides how an event looks on
/// the wire. Emitting through the outbox is the whole point, so the event
/// commits with the effect it reports and never as a side call.
/// </summary>
public static class CloudEventOutbox
{
    /// <summary>Header the bus consumers filter on without parsing the body.</summary>
    public const string EventTypeHeader = "eventType";

    private const string DataContentType = "application/json";

    public static OutboxAppend Build(CloudEventAppend request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["specversion"] = CloudEventParser.SpecVersion,
            ["id"] = Guid.CreateVersion7().ToString("N"),
            ["source"] = request.Source,
            ["type"] = request.Type,
            ["time"] = request.Time,
            ["subject"] = request.Subject,
            ["datacontenttype"] = DataContentType,
            ["data"] = request.Data,
        };
        if (request.Traceparent is { Length: > 0 } traceparent)
        {
            envelope["traceparent"] = traceparent;
        }

        return new OutboxAppend
        {
            Destination = request.Destination,
            Transport = OutboxTransports.Kafka,
            EventType = request.Type,
            MessageKey = request.Subject,
            HeadersJson = request.Traceparent is { Length: > 0 } header
                ? JsonSerializer.Serialize(new { traceparent = header })
                : "{}",
            PayloadJson = JsonSerializer.Serialize(envelope),
            PriorityClass = request.PriorityClass,
        };
    }
}
