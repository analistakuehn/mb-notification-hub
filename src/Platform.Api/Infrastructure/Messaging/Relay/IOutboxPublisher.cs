namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>One message the transport refused; the row stays pending.</summary>
internal sealed record OutboxPublishFailure(Guid MessageId, string Destination, string Reason);

/// <summary>Per-batch publish result: accepted rows get stamped, failed rows stay pending.</summary>
internal sealed class OutboxPublishOutcome
{
    public required IReadOnlyCollection<Guid> AcceptedIds { get; init; }

    public required IReadOnlyList<OutboxPublishFailure> Failures { get; init; }
}

/// <summary>
/// Transport seam of the relay. SQS is the only registered transport today;
/// Kafka publication is the named extension point of this seam: when the
/// first row carries a Kafka destination, a Kafka implementation joins here
/// and a router keyed by destination replaces the single registration.
/// Nothing in the relay loop assumes SQS.
/// </summary>
internal interface IOutboxPublisher
{
    /// <summary>
    /// Publishes one claimed batch and reports, per message, acceptance by the
    /// transport. At-least-once by construction: a message may be published
    /// and still reported failed (crash, timeout), and the pending row then
    /// republishes on a later pass; the consumer owns deduplication.
    /// </summary>
    Task<OutboxPublishOutcome> PublishAsync(
        IReadOnlyList<PendingOutboxMessage> messages,
        CancellationToken cancellationToken);
}
