namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// One bus record as the consumer hands it to a processor: the transport
/// coordinates that identify it, the raw body, and the parsed envelope when it
/// could be read.
/// </summary>
public sealed record KafkaMessageContext
{
    public required string Topic { get; init; }

    public required int Partition { get; init; }

    public required long Offset { get; init; }

    /// <summary>Record key; the subject the producer ordered by.</summary>
    public string? Key { get; init; }

    /// <summary>Record headers decoded as text; a non-text header is dropped.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body exactly as it arrived; the dead-letter record is built from it.</summary>
    public required string Body { get; init; }

    /// <summary>The parsed envelope, or null when the record is permanently unreadable.</summary>
    public CloudEvent? Event { get; init; }

    /// <summary>Stable technical reason the record is unreadable; set exactly when <see cref="Event"/> is null.</summary>
    public string? InvalidReason { get; init; }

    /// <summary>Stable identity of this record for the deduplication mark.</summary>
    public string DedupeId => $"{Topic}:{Partition}:{Offset}";
}

/// <summary>
/// How one consumed bus record must be settled. Deliberately not the queue
/// vocabulary: a log has no per-message visibility, no delete, and no redrive
/// policy, and it does have something a queue does not, a partition the
/// consumer can stop reading without giving the record back.
/// </summary>
public abstract record KafkaDisposition
{
    private KafkaDisposition()
    {
    }

    /// <summary>The effect committed; the offset may advance.</summary>
    public sealed record Processed : KafkaDisposition;

    /// <summary>
    /// The deduplication mark already existed: a redelivery after a
    /// rebalance. No effect happened and the offset may advance.
    /// </summary>
    public sealed record Duplicate : KafkaDisposition;

    /// <summary>
    /// The record is permanently unprocessable and the processor already
    /// recorded it on the dead-letter topic and committed its trail. The
    /// offset may advance; redelivery would only reproduce the same refusal.
    /// </summary>
    public sealed record DeadLetter(string Reason) : KafkaDisposition;

    /// <summary>
    /// The failure is transient and nothing committed. The offset must not
    /// advance and the consumer stops reading the partition until the
    /// dependency recovers, which is how a log applies backpressure.
    /// </summary>
    public sealed record Retry(string Reason) : KafkaDisposition;
}

/// <summary>
/// One consumer-side handler of bus records. The processor owns the
/// transaction of its effect and must verify the deduplication mark inside
/// that same transaction, through <see cref="IProcessedMessageStore"/>, so a
/// redelivery after a commit is detected as a duplicate instead of repeating
/// the effect.
/// </summary>
public interface IKafkaMessageProcessor
{
    /// <summary>Stable consumer name recorded with every deduplication mark.</summary>
    string Consumer { get; }

    Task<KafkaDisposition> ProcessAsync(KafkaMessageContext context, CancellationToken cancellationToken);
}
