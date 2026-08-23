namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// How one processed message must be settled on the queue. Anything the
/// processor cannot classify is thrown instead: an unexpected exception is a
/// transient failure by contract, the message returns with backoff and only
/// the redrive policy ever moves it to the DLQ.
/// </summary>
public abstract record MessageDisposition
{
    private MessageDisposition()
    {
    }

    /// <summary>The effect committed; the consumer deletes the message.</summary>
    public sealed record Processed : MessageDisposition;

    /// <summary>
    /// The dedupe mark already existed: a redelivery after a successful
    /// commit. No effect happened and the consumer deletes the message.
    /// </summary>
    public sealed record Duplicate : MessageDisposition;

    /// <summary>
    /// The message is permanently unprocessable for the given stable reason.
    /// The consumer records the discard through the poison sink and only then
    /// deletes the message.
    /// </summary>
    public sealed record Discard(string Reason) : MessageDisposition;

    /// <summary>
    /// The processor deliberately returned the message to the queue with an
    /// explicit delay: no effect committed and the message must come back
    /// after <see cref="Delay"/> (null falls back to the consumer's standard
    /// backoff). Unlike the exception path, this is a decision, not a
    /// failure: the consumer applies it through a visibility change and never
    /// counts it against the redrive policy semantics of an error.
    /// </summary>
    public sealed record Postponed(TimeSpan? Delay, string Reason) : MessageDisposition;
}

/// <summary>
/// One consumer-side message handler. The processor owns the transaction of
/// its effect and must verify the dedupe mark inside that same transaction,
/// through <see cref="IProcessedMessageStore"/>, so a redelivery after a
/// commit is detected as a duplicate instead of repeating the effect.
/// </summary>
public interface ISqsMessageProcessor
{
    /// <summary>Stable consumer name recorded with every dedupe mark.</summary>
    string Consumer { get; }

    /// <summary>
    /// Whether this consumer understands the message type and schema version.
    /// A refused combination is a permanent error: the consumer discards the
    /// message with a trail instead of retrying forever.
    /// </summary>
    bool Accepts(string type, int schemaVersion);

    Task<MessageDisposition> ProcessAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}
