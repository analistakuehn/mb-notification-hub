using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Events;

/// <summary>One rejection of the lifecycle, as the corporate bus sees it.</summary>
internal sealed record NotificationRejected
{
    /// <summary>Subject of the event and record key; keeps per-recipient order on the topic.</summary>
    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required string TemplateKey { get; init; }

    /// <summary>Member of the canonical rejection-reason catalog.</summary>
    public required string Reason { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Absent when the ingestion rejected: no notification row exists at that
    /// point, and the idempotency key is the correlation the producer holds.
    /// </summary>
    public Guid? NotificationId { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    public string? Traceparent { get; init; }
}

/// <summary>One notification that reached a terminal failure.</summary>
internal sealed record NotificationFailed
{
    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required Guid NotificationId { get; init; }

    /// <summary>Reason of the failure; the delivery vocabulary, not the rejection catalog.</summary>
    public required string Reason { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Channel of the last attempt; absent when the notification expired before any channel.</summary>
    public string? LastChannel { get; init; }

    public string? CorrelationId { get; init; }

    public string? Traceparent { get; init; }
}

/// <summary>One notification confirmed delivered.</summary>
internal sealed record NotificationDelivered
{
    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required Guid NotificationId { get; init; }

    public required string Channel { get; init; }

    public required DateTimeOffset DeliveredAt { get; init; }

    public string? CorrelationId { get; init; }

    public string? Traceparent { get; init; }
}

/// <summary>
/// Outgoing integration vocabulary of this context and the builders that turn
/// one lifecycle fact into an outbox row. The event never carries rendered
/// content nor contact data: whoever needs the detail reads the authorized
/// query or audit surface.
///
/// Every builder returns an <see cref="OutboxAppend"/> on purpose: an
/// integration event is only true if the effect it reports committed, so it
/// is written inside that effect's transaction and published by the relay,
/// never produced directly from a handler.
/// </summary>
internal static class NotificationEvents
{
    /// <summary>Outgoing topic of the corporate bus, shared by every module that emits.</summary>
    internal const string Topic = OutgoingEventBus.Topic;

    /// <summary>URN of this hub as the emitting system.</summary>
    internal const string Source = OutgoingEventBus.Source;

    internal const string RejectedType = "araia.notification.rejected.v1";
    internal const string DeliveredType = "araia.notification.delivered.v1";
    internal const string FailedType = "araia.notification.failed.v1";

    private static readonly JsonSerializerOptions DataOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static OutboxAppend Rejected(NotificationRejected rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return CloudEventOutbox.Build(new CloudEventAppend
        {
            Destination = Topic,
            Source = Source,
            Type = RejectedType,
            Subject = rejection.RecipientId,
            Time = rejection.OccurredAt,
            PriorityClass = rejection.Class,
            Traceparent = rejection.Traceparent,
            Data = JsonSerializer.SerializeToElement(
                new
                {
                    notificationId = rejection.NotificationId,
                    idempotencyKey = rejection.IdempotencyKey,
                    reason = rejection.Reason,
                    @class = rejection.Class,
                    templateKey = rejection.TemplateKey,
                    correlationId = rejection.CorrelationId,
                },
                DataOptions),
        });
    }

    internal static OutboxAppend Failed(NotificationFailed failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return CloudEventOutbox.Build(new CloudEventAppend
        {
            Destination = Topic,
            Source = Source,
            Type = FailedType,
            Subject = failure.RecipientId,
            Time = failure.OccurredAt,
            PriorityClass = failure.Class,
            Traceparent = failure.Traceparent,
            Data = JsonSerializer.SerializeToElement(
                new
                {
                    notificationId = failure.NotificationId,
                    lastChannel = failure.LastChannel,
                    reason = failure.Reason,
                    correlationId = failure.CorrelationId,
                },
                DataOptions),
        });
    }

    internal static OutboxAppend Delivered(NotificationDelivered delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return CloudEventOutbox.Build(new CloudEventAppend
        {
            Destination = Topic,
            Source = Source,
            Type = DeliveredType,
            Subject = delivery.RecipientId,
            Time = delivery.DeliveredAt,
            PriorityClass = delivery.Class,
            Traceparent = delivery.Traceparent,
            Data = JsonSerializer.SerializeToElement(
                new
                {
                    notificationId = delivery.NotificationId,
                    channel = delivery.Channel,
                    deliveredAt = delivery.DeliveredAt,
                    correlationId = delivery.CorrelationId,
                },
                DataOptions),
        });
    }
}
