using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Builder of the internal queue message the delivery-tracking ingestion
/// produces, in the ratified claim-check envelope: the identifier of the
/// stored evidence and nothing else, because the row already holds the sealed
/// provider payload and the queue must never carry it.
/// </summary>
internal static class DeliveryTrackingMessages
{
    internal const string EventReceivedType = "delivery.event_received";
    internal const int SchemaVersion = 1;

    /// <summary>Queue that carries provider feedback to its asynchronous application.</summary>
    internal const string Destination = "delivery-events";

    /// <summary>
    /// Priority class stamped on the relay row. It is a fixed class rather
    /// than the class of the notification: the correlation may still be
    /// unresolved at insert time, and the lookup that would resolve it costs
    /// more than the whole latency budget of a provider callback. The
    /// destination is not an authentication destination, so the row can never
    /// reach the band that protects the latency of an authentication code,
    /// which is the property that matters; the deadline-sensitive path is the
    /// scheduler's, never this one.
    /// </summary>
    internal const string PriorityClass = NotificationClasses.Transactional;

    /// <summary>One stored piece of provider feedback announced for application.</summary>
    internal static OutboxAppend BuildEventReceived(
        Guid deliveryEventId,
        DateTimeOffset occurredAt,
        string? traceparent)
        => new()
        {
            Destination = Destination,
            EventType = EventReceivedType,
            MessageKey = deliveryEventId.ToString(),
            HeadersJson = traceparent is null
                ? "{}"
                : JsonSerializer.Serialize(new { traceparent }),
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = Guid.CreateVersion7(),
                type = EventReceivedType,
                schemaVersion = SchemaVersion,
                occurredAt,
                traceparent,
                priorityClass = PriorityClass,
                payload = new { deliveryEventId },
            }),
            PriorityClass = PriorityClass,
        };
}
