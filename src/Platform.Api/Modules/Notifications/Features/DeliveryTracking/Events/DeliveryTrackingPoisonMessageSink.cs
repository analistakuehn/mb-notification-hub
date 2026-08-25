using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// Discard trail of the delivery-feedback consumer: the audit event and the
/// processed mark commit in one transaction before the consumer deletes the
/// message, so feedback never disappears without a trace and a redelivered
/// poison body resolves on its own mark. Never the raw body in the trail: a
/// message this consumer refused could carry anything.
/// </summary>
internal sealed class DeliveryTrackingPoisonMessageSink(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IAuditTrail auditTrail,
    TimeProvider timeProvider) : IPoisonMessageSink
{
    /// <summary>Action of a message this consumer refuses permanently.</summary>
    internal const string MessageDiscarded = "message.discarded";

    /// <summary>Entity type of a discarded queue message.</summary>
    internal const string EntityTypeMessage = "message";

    public async Task RecordDiscardAsync(PoisonMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DiscardMarkId(message),
            DeliveryStateApplier.ConsumerName,
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
                ActorType = DeliveryTrackingAuditVocabulary.ActorTypeSystem,
                ActorId = DeliveryTrackingAuditVocabulary.ActorIdDeliveryTracker,
                Application = null,
                Action = MessageDiscarded,
                EntityType = EntityTypeMessage,
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
