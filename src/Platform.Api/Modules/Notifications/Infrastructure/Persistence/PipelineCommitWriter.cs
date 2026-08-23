using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Events;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// The transactional invariant of the pipeline: the notification transition,
/// the first attempt, the policy_evaluation rows, the outbox message to the
/// dispatch queue, the audit event and the consumer dedupe mark commit in one
/// database transaction or not at all. The dedupe mark goes first, so a
/// redelivery after a successful commit resolves as a duplicate with zero
/// effect; the commit follows the audit append immediately because the append
/// holds the partition chain lock until the transaction ends.
/// </summary>
internal sealed class PipelineCommitWriter(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<PipelineCommitWriter> logger) : IPipelineCommitter
{
    internal const string ConsumerName = "core-pipeline";
    internal const string AttemptQueuedMessageType = DispatchMessages.AttemptQueuedType;

    public async Task<PipelineCommitResult> CommitAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        PipelineResultKind kind = ResolveKind(context);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DedupeMessageId(context.EnvelopeMessageId, context.Notification.Id),
            ConsumerName,
            cancellationToken);
        if (!marked)
        {
            // Redelivery after a successful commit: rollback leaves no trace.
            return new PipelineCommitResult.Duplicate();
        }

        NotificationAttempt? attempt = ApplyTransition(context, kind, now);
        db.PolicyEvaluations.AddRange(context.PolicyEvaluations);
        await db.SaveChangesAsync(cancellationToken);

        if (attempt is not null)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(),
                BuildDispatchMessage(context, attempt, now),
                cancellationToken);
        }

        // Before the audit append on purpose: the append takes the partition
        // chain lock and holds it until the transaction ends, so every write
        // queued after it stretches the window concurrent ingestion waits on.
        if (BuildIntegrationEvent(context, kind, now) is { } integrationEvent)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(), integrationEvent, cancellationToken);
        }

        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            BuildAuditEntry(context, kind, attempt, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (kind == PipelineResultKind.Deferred)
        {
            logger.NotificationDeferred(
                context.Notification.Id, context.Notification.Class, context.DeferReleaseAt!.Value);
        }

        return new PipelineCommitResult.Committed(kind);
    }

    internal static string DedupeMessageId(Guid envelopeMessageId, Guid notificationId)
        => $"{envelopeMessageId:N}:{notificationId:N}";

    private static PipelineResultKind ResolveKind(NotificationContext context)
    {
        IReadOnlyList<StageTraceEntry> trace = context.Trace.Entries;
        StageOutcome last = trace.Count > 0 ? trace[^1].Outcome : StageOutcome.Continue;
        return last switch
        {
            StageOutcome.Continue => PipelineResultKind.Dispatched,
            StageOutcome.Defer => PipelineResultKind.Deferred,
            StageOutcome.Reject when context.Expired => PipelineResultKind.Expired,
            StageOutcome.Reject => PipelineResultKind.Rejected,
            _ => throw new InvalidOperationException($"Desfecho de estágio não suportado: {last}."),
        };
    }

    private NotificationAttempt? ApplyTransition(
        NotificationContext context,
        PipelineResultKind kind,
        DateTimeOffset now)
    {
        Notification notification = context.Notification;
        switch (kind)
        {
            case PipelineResultKind.Expired:
                notification.MarkExpired();
                return null;
            case PipelineResultKind.Rejected:
                notification.MarkRejected(context.Policy?.Version);
                return null;
            case PipelineResultKind.Deferred:
                notification.MarkDeferred(
                    context.DeferReleaseAt
                        ?? throw new InvalidOperationException("Um adiamento requer o instante de liberação."),
                    RequiredPolicyVersion(context));
                return null;
            case PipelineResultKind.Dispatched:
                if (context.Render is { } render && render.Version != notification.TemplateVersion)
                {
                    notification.RestampTemplateVersion(render.Version);
                }

                notification.MarkDispatched(RequiredPolicyVersion(context));
                var attempt = NotificationAttempt.Queue(new NotificationAttemptDraft
                {
                    NotificationId = notification.Id,
                    Sequence = 1,
                    Channel = context.Render!.Channel,
                    ContactPointId = context.SelectedContactPointId,
                    RenderedContentEncrypted = context.RenderedContentEncrypted
                        ?? throw new InvalidOperationException(
                            "Um attempt enfileirado requer o conteúdo renderizado cifrado."),
                    ContentHashFull = context.Render.Full.ContentHash,
                    ContentHashMasked = (context.Render.Masked ?? context.Render.Full).ContentHash,
                    FallbackTimeout = context.FallbackTimeout,
                    QueuedAt = now,
                });
                db.NotificationAttempts.Add(attempt);
                return attempt;
            default:
                throw new InvalidOperationException($"Desfecho de pipeline não suportado: {kind}.");
        }
    }

    private static int RequiredPolicyVersion(NotificationContext context)
        => context.Policy?.Version
            ?? throw new InvalidOperationException(
                "O desfecho requer a política publicada carregada pelo estágio Policy.");

    private static OutboxAppend BuildDispatchMessage(
        NotificationContext context,
        NotificationAttempt attempt,
        DateTimeOffset now)
    {
        var destination = context.DispatchDestination
            ?? throw new InvalidOperationException("O estágio Route não definiu a fila de dispatch.");
        return DispatchMessages.BuildAttemptQueued(
            destination,
            context.Notification.RecipientId,
            context.Notification.Class,
            context.Notification.Id,
            attempt.Id,
            now,
            Activity.Current?.Id);
    }

    /// <summary>
    /// The outgoing integration event of this outcome, or null when the
    /// outcome has none. A dispatched notification announces nothing on the
    /// bus (the producer already holds its acceptance) and a deferral is not a
    /// result yet; a rejection and an expiration are both terminal answers the
    /// producer is owed.
    /// </summary>
    private static OutboxAppend? BuildIntegrationEvent(
        NotificationContext context,
        PipelineResultKind kind,
        DateTimeOffset now)
    {
        Notification notification = context.Notification;
        return kind switch
        {
            PipelineResultKind.Rejected => NotificationEvents.Rejected(new NotificationRejected
            {
                RecipientId = notification.RecipientId,
                Class = notification.Class,
                TemplateKey = notification.TemplateKey,
                Reason = context.LastReason
                    ?? throw new InvalidOperationException(
                        "Uma rejeição do pipeline requer o motivo canônico do estágio."),
                NotificationId = notification.Id,
                IdempotencyKey = notification.IdempotencyKey,
                CorrelationId = notification.CorrelationId,
                OccurredAt = now,
                Traceparent = Activity.Current?.Id,
            }),
            PipelineResultKind.Expired => NotificationEvents.Failed(new NotificationFailed
            {
                RecipientId = notification.RecipientId,
                Class = notification.Class,
                NotificationId = notification.Id,
                Reason = NotificationRejectionReasons.Expired,
                CorrelationId = notification.CorrelationId,
                OccurredAt = now,
                Traceparent = Activity.Current?.Id,
            }),
            _ => null,
        };
    }

    private static AuditEntry BuildAuditEntry(
        NotificationContext context,
        PipelineResultKind kind,
        NotificationAttempt? attempt,
        DateTimeOffset now)
    {
        var action = kind switch
        {
            PipelineResultKind.Dispatched => PipelineAuditVocabulary.NotificationDispatched,
            PipelineResultKind.Rejected => PipelineAuditVocabulary.NotificationRejected,
            PipelineResultKind.Deferred => PipelineAuditVocabulary.NotificationDeferred,
            PipelineResultKind.Expired => PipelineAuditVocabulary.NotificationExpired,
            _ => throw new InvalidOperationException($"Desfecho de pipeline não suportado: {kind}."),
        };
        return new AuditEntry
        {
            ActorType = PipelineAuditVocabulary.ActorTypeSystem,
            ActorId = PipelineAuditVocabulary.ActorIdCoreWorker,
            Application = context.Notification.Application,
            Action = action,
            EntityType = PipelineAuditVocabulary.EntityTypeNotification,
            EntityId = context.Notification.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                @class = context.Notification.Class,
                templateKey = context.Notification.TemplateKey,
                templateVersion = context.Notification.TemplateVersion,
                policyVersion = context.Policy?.Version,
                reason = context.LastReason,
                releaseAt = context.DeferReleaseAt,
                attemptId = attempt?.Id,
                channel = attempt?.Channel,
                destination = attempt is null ? null : context.DispatchDestination,
                stages = context.Trace.Entries
                    .Select(entry => new { entry.Stage, outcome = entry.Outcome.ToString(), entry.Reason }),
            }),
            OccurredAt = now,
        };
    }
}
