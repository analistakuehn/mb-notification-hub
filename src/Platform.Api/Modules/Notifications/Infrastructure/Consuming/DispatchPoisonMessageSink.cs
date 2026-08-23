using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

/// <summary>
/// Discard trail of the dispatch consumer: the audit event and the processed
/// mark commit in one transaction before the consumer deletes the poison
/// message, so nothing ever disappears without a trace and a redelivered
/// poison body resolves on its own mark. Never the raw body in the trail: a
/// malformed message could carry anything.
/// </summary>
internal sealed class DispatchPoisonMessageSink(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IAuditTrail auditTrail,
    TimeProvider timeProvider) : IPoisonMessageSink
{
    public async Task RecordDiscardAsync(PoisonMessage message, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DiscardMarkId(message),
            AttemptDispatchWriter.ConsumerName,
            cancellationToken);
        if (!marked)
        {
            // The discard already committed once; the redelivery only needs the delete.
            return;
        }

        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            new AuditEntry
            {
                ActorType = DispatchingAuditVocabulary.ActorTypeSystem,
                ActorId = DispatchingAuditVocabulary.ActorIdDispatcher,
                Application = null,
                Action = PipelineAuditVocabulary.MessageDiscarded,
                EntityType = DispatchingAuditVocabulary.EntityTypeMessage,
                EntityId = message.EnvelopeMessageId?.ToString() ?? message.SqsMessageId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    queue = message.QueueName,
                    reason = message.Reason,
                    eventType = message.EventType,
                    schemaVersion = message.SchemaVersion,
                    sqsMessageId = message.SqsMessageId,
                }),
                OccurredAt = timeProvider.GetUtcNow(),
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The mark key of a discard: the envelope id when the body exposed one,
    /// otherwise the transport id, which SQS keeps stable across redeliveries.
    /// </summary>
    internal static string DiscardMarkId(PoisonMessage message)
        => message.EnvelopeMessageId is { } envelopeId
            ? $"discard:{envelopeId:N}"
            : $"discard:sqs:{message.SqsMessageId}";
}
