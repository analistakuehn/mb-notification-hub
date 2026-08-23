namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// One consumer-side dedupe mark: the message id joined with the business key,
/// per consumer. The mark is written inside the transaction of the message's
/// effect by <see cref="Consuming.IProcessedMessageStore"/>; the entity exists
/// for migrations, maintenance and the purge job.
/// </summary>
public sealed class ProcessedMessage
{
    // EF Core materialization: fields are populated from the store.
    private ProcessedMessage()
    {
        MessageId = null!;
        Consumer = null!;
    }

    /// <summary>Envelope message id joined with the message's business key.</summary>
    public string MessageId { get; }

    /// <summary>Stable name of the consumer that processed the message.</summary>
    public string Consumer { get; }

    public DateTimeOffset ProcessedAt { get; }
}
