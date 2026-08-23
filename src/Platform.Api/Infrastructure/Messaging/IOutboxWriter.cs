using System.Data.Common;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>Everything the caller supplies to append one outbox message.</summary>
public sealed record OutboxAppend
{
    /// <summary>Logical destination the relay publishes to.</summary>
    public required string Destination { get; init; }

    /// <summary>Type of the enveloped message.</summary>
    public required string EventType { get; init; }

    /// <summary>Ordering key the relay hands to the destination.</summary>
    public required string MessageKey { get; init; }

    /// <summary>Transport headers as a JSON object.</summary>
    public required string HeadersJson { get; init; }

    /// <summary>Full message envelope as JSON.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>Priority class the relay orders its reads by.</summary>
    public required string PriorityClass { get; init; }
}

/// <summary>
/// Transactional write surface of the platform outbox. The append executes its
/// insert on the caller's open database transaction, so the business effect
/// and its outgoing message commit together or not at all. The writer owns the
/// message id and the creation instant; the sent stamp belongs to the relay.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Appends one message inside <paramref name="transaction"/> and returns
    /// the id the writer assigned to it.
    /// </summary>
    Task<Guid> AppendAsync(DbTransaction transaction, OutboxAppend message, CancellationToken cancellationToken);
}
