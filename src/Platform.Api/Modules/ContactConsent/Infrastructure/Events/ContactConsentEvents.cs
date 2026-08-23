using System.Diagnostics;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;

/// <summary>
/// Builds the cache-invalidation messages this module appends to the platform
/// outbox in the same transaction as the write. The payload is a claim check:
/// only the recipient id and, when one row was affected, the contact point id.
/// Contact values, tokens and profile data never enter the bus; consumers
/// re-read state through the published contract.
/// </summary>
internal static class ContactConsentEvents
{
    internal const string ContactChanged = "contact.changed";
    internal const string ConsentChanged = "consent.changed";
    internal const string Destination = "contacts-changed";
    internal const string PriorityClass = "transactional";

    internal static OutboxAppend Build(
        string eventType,
        string recipientId,
        Guid? contactPointId,
        DateTimeOffset occurredAt)
    {
        var traceparent = Activity.Current?.Id;
        return new OutboxAppend
        {
            Destination = Destination,
            EventType = eventType,
            MessageKey = recipientId,
            HeadersJson = traceparent is null
                ? "{}"
                : JsonSerializer.Serialize(new { traceparent }),
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = Guid.CreateVersion7(),
                type = eventType,
                schemaVersion = 1,
                occurredAt,
                traceparent,
                priorityClass = PriorityClass,
                payload = new { recipientId, contactPointId },
            }),
            PriorityClass = PriorityClass,
        };
    }
}
