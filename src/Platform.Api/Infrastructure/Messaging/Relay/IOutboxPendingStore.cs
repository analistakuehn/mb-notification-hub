namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// One pending row as the relay reads it: the JSON columns come back as the
/// exact text the database stores, so the publisher can ship the payload
/// without re-serializing it.
/// </summary>
internal sealed record PendingOutboxMessage(
    Guid Id,
    string Destination,
    string EventType,
    string MessageKey,
    string HeadersJson,
    string PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>
/// A claimed batch of pending rows, locked until the claim completes or is
/// disposed. Completing stamps <c>sent_at</c> on the accepted rows and
/// commits; disposing an uncompleted claim rolls back, so the rows unlock and
/// stay pending for the next pass. A crash between publish and stamp
/// therefore republishes on the next pass: duplicate accepted, loss never.
/// </summary>
internal interface IOutboxClaim : IAsyncDisposable
{
    IReadOnlyList<PendingOutboxMessage> Messages { get; }

    Task CompleteAsync(
        IReadOnlyCollection<Guid> sentIds,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the relay over the platform outbox. Claims run with
/// <c>FOR UPDATE SKIP LOCKED</c>, so concurrent relay instances drain the
/// same table without coordination and without double-claiming a row.
/// </summary>
internal interface IOutboxPendingStore
{
    Task<IOutboxClaim> ClaimAsync(OutboxBand band, int batchSize, CancellationToken cancellationToken);
}
