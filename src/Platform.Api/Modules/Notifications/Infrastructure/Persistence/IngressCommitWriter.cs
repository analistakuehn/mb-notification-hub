using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Commits what one consumed bus event leaves behind: the trail the ingestion
/// use case held, and the deduplication mark that makes a redelivery after a
/// rebalance harmless.
///
/// The mark goes first. When it already exists, this event was fully settled
/// before, the transaction rolls back, and nothing is written twice. Only then
/// the held trail is flushed, with each outgoing event ahead of its audit
/// append, because the append holds the partition chain lock until the
/// transaction ends.
/// </summary>
internal sealed class IngressCommitWriter(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail)
{
    internal const string ConsumerName = "kafka-ingress";

    /// <summary>
    /// Returns false when the deduplication mark already existed, which means
    /// the caller must settle the record as a duplicate.
    /// </summary>
    public async Task<bool> TryCommitAsync(
        string dedupeId,
        DeferredTrailIngestionSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(), dedupeId, ConsumerName, cancellationToken);
        if (!marked)
        {
            return false;
        }

        await sink.FlushAsync(
            transaction.GetDbTransaction(), outboxWriter, auditTrail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
