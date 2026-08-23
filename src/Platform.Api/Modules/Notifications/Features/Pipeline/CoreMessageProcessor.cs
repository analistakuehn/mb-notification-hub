using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline;

/// <summary>
/// Consumer-side entry of the Core pipeline. An accepted notification runs
/// the ordered stage list; a fallback trigger routes to the fallback
/// handler, because both message types share the core queues. The commit
/// carries the dedupe mark, so a redelivery after a successful commit
/// resolves as a duplicate here; a notification already past its accepted
/// state resolves the same way, with the duplicate trail the at-least-once
/// contract demands.
/// </summary>
internal sealed class CoreMessageProcessor(
    NotificationsDbContext db,
    NotificationPipeline pipeline,
    PipelineCommitWriter commitWriter,
    FallbackRequestHandler fallbackHandler,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<CoreMessageProcessor> logger) : ISqsMessageProcessor
{
    internal const string AcceptedMessageType = "notification.accepted";
    internal const int SupportedSchemaVersion = 1;
    internal const string ReasonPayloadWithoutNotificationId = "payload-missing-notification-id";
    internal const string ReasonNotificationNotFound = "notification-not-found";

    public string Consumer => PipelineCommitWriter.ConsumerName;

    public bool Accepts(string type, int schemaVersion)
        => schemaVersion == SupportedSchemaVersion
            && (string.Equals(type, AcceptedMessageType, StringComparison.Ordinal)
                || string.Equals(type, DispatchMessages.FallbackRequestedType, StringComparison.Ordinal));

    public async Task<MessageDisposition> ProcessAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (string.Equals(envelope.Type, DispatchMessages.FallbackRequestedType, StringComparison.Ordinal))
        {
            return await fallbackHandler.ProcessAsync(envelope, cancellationToken);
        }

        if (!envelope.Payload.TryGetProperty("notificationId", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out Guid notificationId))
        {
            return new MessageDisposition.Discard(ReasonPayloadWithoutNotificationId);
        }

        Notification? notification = await db.Notifications
            .FirstOrDefaultAsync(candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null)
        {
            // The outbox commits with the notification, so an absent row means
            // the claim check outlived its state (purged partition, foreign
            // environment): permanently unprocessable, never retryable.
            return new MessageDisposition.Discard(ReasonNotificationNotFound);
        }

        if (notification.Status != NotificationStatuses.Accepted)
        {
            await RecordDuplicateAsync(notification, cancellationToken);
            logger.PipelineDuplicateSkipped(notification.Id, notification.Status);
            return new MessageDisposition.Duplicate();
        }

        var context = new NotificationContext(notification, envelope.MessageId, commitWriter);
        PipelineCommitResult result = await pipeline.RunAsync(context, cancellationToken);
        switch (result)
        {
            case PipelineCommitResult.Committed committed:
                var kind = committed.Kind.ToString();
                logger.PipelineCompleted(notification.Id, notification.Class, kind);
                return new MessageDisposition.Processed();
            case PipelineCommitResult.Duplicate:
                await RecordDuplicateAsync(notification, cancellationToken);
                logger.PipelineDuplicateSkipped(notification.Id, notification.Status);
                return new MessageDisposition.Duplicate();
            default:
                throw new InvalidOperationException(
                    $"Resultado de commit não suportado: {result.GetType().Name}.");
        }
    }

    /// <summary>
    /// A detected redelivery leaves the duplicate trail in its own short
    /// transaction: visible in the audit, never a second effect.
    /// </summary>
    private async Task RecordDuplicateAsync(Notification notification, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            new AuditEntry
            {
                ActorType = PipelineAuditVocabulary.ActorTypeSystem,
                ActorId = PipelineAuditVocabulary.ActorIdCoreWorker,
                Application = notification.Application,
                Action = PipelineAuditVocabulary.NotificationDuplicate,
                EntityType = PipelineAuditVocabulary.EntityTypeNotification,
                EntityId = notification.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    source = "core-pipeline",
                    status = notification.Status,
                }),
                OccurredAt = timeProvider.GetUtcNow(),
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
