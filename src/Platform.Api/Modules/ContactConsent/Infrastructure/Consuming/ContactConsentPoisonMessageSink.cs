using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

/// <summary>
/// Discard trail of the contacts-changed consumer: the audit event and the
/// processed mark commit in one transaction before the consumer deletes the
/// poison message, so nothing ever disappears without a trace. Never the raw
/// body in the trail: a malformed message could carry anything.
/// </summary>
internal sealed class ContactConsentPoisonMessageSink(
    ContactConsentDbContext db,
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
            ContactsChangedProcessor.ConsumerName,
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
                ActorType = ContactConsentAuditVocabulary.ActorTypeSystem,
                ActorId = ContactConsentAuditVocabulary.ActorIdCacheWorker,
                Application = null,
                Action = ContactConsentAuditVocabulary.MessageDiscarded,
                EntityType = ContactConsentAuditVocabulary.EntityTypeMessage,
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

    private static string DiscardMarkId(PoisonMessage message)
        => message.EnvelopeMessageId is { } envelopeId
            ? $"discard:{envelopeId:N}"
            : $"discard:sqs:{message.SqsMessageId}";
}
