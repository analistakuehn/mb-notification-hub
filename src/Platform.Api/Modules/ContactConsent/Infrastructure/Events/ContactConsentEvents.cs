using System.Diagnostics;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;

/// <summary>One consent record as the corporate bus sees it.</summary>
internal sealed record ConsentChangedFact
{
    /// <summary>Subject of the event and record key; keeps per-recipient order on the topic.</summary>
    public required string RecipientId { get; init; }

    public required string Channel { get; init; }

    public required string Purpose { get; init; }

    public required bool Granted { get; init; }

    /// <summary>Where the stance was collected: app, atendimento or importação.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Builds the messages this module appends to the platform outbox in the same
/// transaction as the write.
///
/// Two of them, and they are not the same message. The internal invalidation
/// is a claim check that never leaves the hub: only the recipient id and, when
/// one row was affected, the contact point id, so contact values, tokens and
/// profile data never enter a queue and consumers re-read state through the
/// published contract. The outgoing consent event tells the domains that a
/// recipient's stance on a purpose changed, and carries no contact value and
/// no evidence beyond the origin that recorded it.
/// </summary>
internal static class ContactConsentEvents
{
    internal const string ContactChanged = "contact.changed";
    internal const string ConsentChanged = "consent.changed";
    internal const string Destination = "contacts-changed";
    internal const string PriorityClass = "transactional";

    /// <summary>Outgoing event type of a consent change on the corporate bus.</summary>
    internal const string ConsentChangedEventType = "araia.notification.consent_changed.v1";

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

    /// <summary>
    /// Announces one consent record on the outgoing topic of the hub, next to
    /// the internal invalidation message of the same write. A declaration that
    /// changed nothing appends no record, so it announces nothing either.
    /// </summary>
    internal static OutboxAppend BuildConsentChanged(ConsentChangedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return CloudEventOutbox.Build(new CloudEventAppend
        {
            Destination = OutgoingEventBus.Topic,
            Source = OutgoingEventBus.Source,
            Type = ConsentChangedEventType,
            Subject = fact.RecipientId,
            Time = fact.OccurredAt,
            PriorityClass = PriorityClass,
            Traceparent = Activity.Current?.Id,
            Data = JsonSerializer.SerializeToElement(new
            {
                recipientId = fact.RecipientId,
                channel = fact.Channel,
                purpose = fact.Purpose,
                granted = fact.Granted,
                source = fact.Source,
            }),
        });
    }
}
