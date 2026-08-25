using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>How one piece of canonical feedback settled against the attempt it describes.</summary>
internal enum DeliveryApplicationOutcome
{
    /// <summary>The attempt moved, the evidence is stamped and the trail is written.</summary>
    Applied = 0,

    /// <summary>
    /// The feedback names no transition out of the stored status, or the
    /// status moved underneath it. Nothing was written to the attempt and the
    /// evidence stays stored and unapplied.
    /// </summary>
    Ignored = 1,

    /// <summary>
    /// No attempt matches this feedback yet. Not a failure: a provider may
    /// call back before the transaction that recorded the send commits.
    /// </summary>
    AttemptUnresolved = 2,

    /// <summary>The dedupe mark already existed: a redelivery after a commit.</summary>
    Duplicate = 3,
}

/// <summary>
/// Everything one application needs. The canonical event is the whole input on
/// purpose: the same record arrives from a webhook and from a later
/// reconciliation query, and neither path may own a state machine of its own.
/// </summary>
internal sealed record DeliveryApplicationRequest
{
    /// <summary>Canonical feedback to apply.</summary>
    public required ProviderDeliveryEvent Event { get; init; }

    /// <summary>
    /// Stored evidence this feedback came from. Null when the caller keeps no
    /// evidence row, in which case nothing is stamped and only the attempt
    /// moves.
    /// </summary>
    public Guid? DeliveryEventId { get; init; }

    /// <summary>
    /// Deduplication identity of the queue message that drove this call, so
    /// the mark commits with the effect. Null when no queue message drove it.
    /// </summary>
    public string? DedupeMessageId { get; init; }
}

/// <summary>
/// Applies one piece of canonical delivery feedback to the attempt it
/// describes. This is the single writer of the feedback-driven half of the
/// attempt state machine: the asynchronous consumer of provider callbacks
/// calls it, and the reconciliation that queries providers for the attempts no
/// callback ever settled calls the same code with the same record, so the two
/// sources can never drift into two machines.
/// <para>
/// The correlation route is ordered: the identifiers the provider echoed back
/// first, and the provider's own message identity second. The second route
/// exists because one provider echoes nothing at all, and it is what the
/// partial index over the attempt's provider message identity serves.
/// </para>
/// <para>
/// The audit append belongs here and not in the request that received the
/// callback: the append holds the chain lock of the trail's monthly partition
/// until the transaction ends, and a provider that decides its own callback
/// rate would otherwise serialize this hub's ingestion behind its feedback.
/// </para>
/// <para>
/// What the transition means for the notification is not decided here. A
/// confirmed delivery ends the notification and a refused destination may
/// exhaust the plan, and both conclusions already have an owner: this applier
/// calls that owner inside its own transaction instead of restating the rule,
/// so the feedback path and the synchronous verdict can never conclude one
/// notification in two different ways.
/// </para>
/// </summary>
internal sealed class DeliveryStateApplier(
    NotificationsDbContext db,
    IAuditTrail auditTrail,
    IOutboxWriter outboxWriter,
    IProcessedMessageStore processedMessages,
    TimeProvider timeProvider,
    ILogger<DeliveryStateApplier> logger)
{
    /// <summary>Stable consumer name recorded with every dedupe mark of this path.</summary>
    internal const string ConsumerName = "delivery-tracker";

    /// <summary>Joins the queue message identity with the evidence it carried.</summary>
    internal static string DedupeMessageId(Guid envelopeMessageId, Guid deliveryEventId)
        => $"{envelopeMessageId:N}:{deliveryEventId:N}";

    public async Task<DeliveryApplicationOutcome> ApplyAsync(
        DeliveryApplicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderDeliveryEvent providerEvent = request.Event;

        NotificationAttempt? attempt = await ResolveAttemptAsync(providerEvent, cancellationToken);
        if (attempt is null)
        {
            logger.DeliveryAttemptUnresolved(providerEvent.ProviderKey);
            return DeliveryApplicationOutcome.AttemptUnresolved;
        }

        var fromStatus = attempt.Status;
        var toStatus = DeliveryStateMachine.NextStatus(fromStatus, providerEvent.Kind);
        if (toStatus is null)
        {
            var reportedKind = DeliveryEventKinds.From(providerEvent.Kind);
            logger.DeliveryTransitionNotApplicable(
                reportedKind, providerEvent.ProviderKey, fromStatus, attempt.Id);
            return await SettleWithoutTransitionAsync(request, cancellationToken);
        }

        // Tracked on purpose: a transition that concludes the notification
        // writes that conclusion through the same entity the dispatch side
        // writes it through.
        Notification? notification = await db.Notifications
            .FirstOrDefaultAsync(
                candidate => candidate.Id == attempt.NotificationId, cancellationToken);
        if (notification is null)
        {
            logger.DeliveryNotificationMissing(attempt.Id, attempt.NotificationId);
            return await SettleWithoutTransitionAsync(request, cancellationToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await TryMarkAsync(request, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeliveryApplicationOutcome.Duplicate;
        }

        var moved = await db.NotificationAttempts
            .Where(candidate => candidate.Id == attempt.Id && candidate.Status == fromStatus)
            .ExecuteUpdateAsync(
                setters =>
                {
                    setters.SetProperty(candidate => candidate.Status, toStatus);

                    // This hub's instant, and never the provider's: the stamp
                    // answers how long the attempt has been in the state it is
                    // in, and a provider that dates its own event backwards
                    // would otherwise make a row that just moved read as old
                    // enough for an age-driven scan to act on.
                    setters.SetProperty(candidate => candidate.StatusChangedAt, now);
                    ApplyDeliveryStamp(setters, toStatus, providerEvent.OccurredAt);
                    ApplyErrorCode(setters, toStatus, providerEvent.ErrorCode);
                },
                cancellationToken);
        if (moved != 1)
        {
            // The status moved between the read and the guarded write. The
            // feedback is not lost: the evidence row keeps it unapplied and a
            // reconciliation may revisit it.
            await transaction.RollbackAsync(cancellationToken);
            logger.DeliveryTransitionRaced(attempt.Id, fromStatus);
            return DeliveryApplicationOutcome.Ignored;
        }

        await StampEvidenceAsync(request, attempt, now, cancellationToken);
        await ApplyToPlanAsync(
            transaction, notification, attempt, providerEvent, toStatus, now, cancellationToken);

        // Immediately before the commit on purpose: the append takes the
        // partition chain lock and holds it until the transaction ends, so
        // anything queued after it widens the window concurrent ingestion
        // waits on.
        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            BuildAuditEntry(request, notification, attempt, fromStatus, toStatus, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.DeliveryTransitionApplied(
            providerEvent.ProviderKey, attempt.Id, fromStatus, toStatus);
        return DeliveryApplicationOutcome.Applied;
    }

    /// <summary>
    /// Carries the attempt's new state up to the notification, through the
    /// same writes the synchronous verdict uses. A confirmed delivery ends the
    /// notification on delivered; a failure or a bounce that leaves the step
    /// with nothing alive advances the plan, which asks for the next step or
    /// ends the notification on failed exactly as an immediate provider
    /// rejection would have.
    /// </summary>
    private async Task ApplyToPlanAsync(
        IDbContextTransaction transaction,
        Notification notification,
        NotificationAttempt attempt,
        ProviderDeliveryEvent providerEvent,
        string toStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var scope = new PlanOutcomeScope(
            db,
            outboxWriter,
            auditTrail,
            transaction.GetDbTransaction(),
            new PlanOutcomeActor(
                DeliveryTrackingAuditVocabulary.ActorTypeSystem,
                DeliveryTrackingAuditVocabulary.ActorIdDeliveryTracker));

        if (string.Equals(toStatus, NotificationAttemptStatuses.Delivered, StringComparison.Ordinal))
        {
            await NotificationPlanOutcome.ConcludeDeliveredAsync(
                scope,
                notification,
                attempt,
                providerEvent.OccurredAt,
                new
                {
                    attemptId = attempt.Id,
                    channel = attempt.Channel,
                    providerKey = providerEvent.ProviderKey,
                    providerEventId = providerEvent.ProviderEventId,
                },
                now,
                cancellationToken);
            return;
        }

        if (toStatus is not (NotificationAttemptStatuses.Failed or NotificationAttemptStatuses.Bounced)) return;

        if (await NotificationPlanOutcome.IsStepExhaustedAsync(db, attempt, cancellationToken))
        {
            await NotificationPlanOutcome.AdvanceAsync(
                scope,
                notification,
                attempt,
                providerEvent.ErrorCode ?? DeliveryEventKinds.From(providerEvent.Kind),
                now,
                cancellationToken);
        }
    }

    private static void ApplyDeliveryStamp(
        UpdateSettersBuilder<NotificationAttempt> setters,
        string toStatus,
        DateTimeOffset occurredAt)
    {
        if (string.Equals(toStatus, NotificationAttemptStatuses.Delivered, StringComparison.Ordinal))
        {
            // The provider's own instant, not this hub's: the stamp answers
            // when the message arrived, never when the feedback was consumed.
            setters.SetProperty(candidate => candidate.DeliveredAt, occurredAt);
        }
    }

    private static void ApplyErrorCode(
        UpdateSettersBuilder<NotificationAttempt> setters,
        string toStatus,
        string? errorCode)
    {
        if (errorCode is { Length: > 0 }
            && toStatus is NotificationAttemptStatuses.Failed or NotificationAttemptStatuses.Bounced)
        {
            setters.SetProperty(candidate => candidate.ErrorCode, errorCode);
        }
    }

    /// <summary>
    /// Closes the queue message of feedback that changes nothing. The dedupe
    /// mark still commits, so a redelivery resolves as a duplicate instead of
    /// re-reading the same conclusion forever; the evidence row keeps
    /// <c>applied_at</c> empty, because nothing was applied.
    /// </summary>
    private async Task<DeliveryApplicationOutcome> SettleWithoutTransitionAsync(
        DeliveryApplicationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DedupeMessageId is null) return DeliveryApplicationOutcome.Ignored;

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await TryMarkAsync(request, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeliveryApplicationOutcome.Duplicate;
        }

        await transaction.CommitAsync(cancellationToken);
        return DeliveryApplicationOutcome.Ignored;
    }

    private async Task<bool> TryMarkAsync(
        DeliveryApplicationRequest request,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
        => request.DedupeMessageId is not { } messageId
            || await processedMessages.TryMarkAsync(
                transaction.GetDbTransaction(), messageId, ConsumerName, cancellationToken);

    /// <summary>
    /// Stamps the evidence with the instant it was consumed and with the
    /// correlation the application resolved, so feedback joined through the
    /// provider's message identity becomes readable by notification too.
    /// </summary>
    private async Task StampEvidenceAsync(
        DeliveryApplicationRequest request,
        NotificationAttempt attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.DeliveryEventId is not { } deliveryEventId) return;

        await db.DeliveryEvents
            .Where(candidate => candidate.Id == deliveryEventId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.AppliedAt, now)
                    .SetProperty(candidate => candidate.AttemptId, attempt.Id)
                    .SetProperty(candidate => candidate.NotificationId, attempt.NotificationId),
                cancellationToken);
    }

    private async Task<NotificationAttempt?> ResolveAttemptAsync(
        ProviderDeliveryEvent providerEvent,
        CancellationToken cancellationToken)
    {
        if (providerEvent.Correlation is { } correlation)
        {
            NotificationAttempt? correlated = await db.NotificationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == correlation.AttemptId
                        && candidate.NotificationId == correlation.NotificationId,
                    cancellationToken);
            if (correlated is not null) return correlated;
        }

        if (providerEvent.ProviderMessageId is not { Length: > 0 } providerMessageId) return null;

        // The equality implies the index predicate, so the partial index over
        // the attempts that carry a provider message identity answers this.
        // Most recent first: a provider identity is unique in practice, and
        // ordering keeps the answer deterministic if one is ever reused.
        return await db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.ProviderMessageId == providerMessageId
                && candidate.ProviderKey == providerEvent.ProviderKey)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static AuditEntry BuildAuditEntry(
        DeliveryApplicationRequest request,
        Notification notification,
        NotificationAttempt attempt,
        string fromStatus,
        string toStatus,
        DateTimeOffset now)
        => new()
        {
            ActorType = DeliveryTrackingAuditVocabulary.ActorTypeSystem,
            ActorId = DeliveryTrackingAuditVocabulary.ActorIdDeliveryTracker,
            Application = notification.Application,
            Action = DeliveryTrackingAuditVocabulary.DeliveryEventApplied,
            EntityType = DeliveryTrackingAuditVocabulary.EntityTypeNotification,
            EntityId = notification.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                deliveryEventId = request.DeliveryEventId,
                attemptId = attempt.Id,
                channel = attempt.Channel,
                providerKey = request.Event.ProviderKey,
                providerEventId = request.Event.ProviderEventId,
                kind = DeliveryEventKinds.From(request.Event.Kind),
                fromStatus,
                toStatus,
                errorCode = request.Event.ErrorCode,
                occurredAt = request.Event.OccurredAt,
            }),
            OccurredAt = now,
        };
}
