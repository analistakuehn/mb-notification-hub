namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// One record bound for the dead-letter topic. The body is supplied by the
/// caller, never copied blindly from the source record: a control that refuses
/// a message for carrying a secret must not be the thing that copies the
/// secret onto a topic with a longer retention.
/// </summary>
public sealed record DeadLetterRecord
{
    public required string Topic { get; init; }

    /// <summary>Key of the source record; keeps per-subject order on the dead-letter topic too.</summary>
    public string? Key { get; init; }

    /// <summary>Body to publish; the original one, or a redacted form of it.</summary>
    public required string Body { get; init; }

    /// <summary>Diagnostic headers; every value travels as text.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}

/// <summary>
/// Produces the dead-letter record of a permanently invalid bus message. The
/// produce is synchronous by contract: the caller must know the record exists
/// before it marks the source message as handled, because a mark written first
/// would make the replay skip a message nobody ever recorded.
/// </summary>
public interface IKafkaDeadLetterProducer
{
    Task ProduceAsync(DeadLetterRecord record, CancellationToken cancellationToken);
}
