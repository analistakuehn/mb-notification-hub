using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Events;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>Who a plan outcome is recorded as on the audit trail.</summary>
internal readonly record struct PlanOutcomeActor(string ActorType, string ActorId);

/// <summary>
/// The collaborators one plan outcome writes through, all bound to the
/// caller's open transaction: the outcome is never a transaction of its own,
/// it always joins the transaction of the verdict that produced it.
/// </summary>
internal readonly record struct PlanOutcomeScope(
    NotificationsDbContext Db,
    IOutboxWriter OutboxWriter,
    IAuditTrail AuditTrail,
    DbTransaction Transaction,
    PlanOutcomeActor Actor);

/// <summary>
/// What ends one step of a delivery plan, written once and shared by every
/// path that can end one: the synchronous provider verdict, the provider
/// feedback that arrives later, and the elapsed deadline. Two sources of the
/// same conclusion must not become two rules, which is the same reason the
/// feedback state machine is a single table.
/// <para>
/// The unicity of the advance is a claim in the database and not a
/// deduplication of messages. The reactive trigger and the deadline trigger
/// are two distinct queue rows with two distinct message identities, so both
/// dedupe marks pass; only a conditional write over the state they share can
/// decide which one advances the plan. The claim is per step and not per
/// attempt, because a push fan-out creates siblings that share one absolute
/// deadline and two expired siblings would otherwise each buy the same next
/// step, which means two messages to the same person.
/// </para>
/// </summary>
internal static class NotificationPlanOutcome
{
    /// <summary>
    /// Widest age the attempts of one notification can reach, and the window
    /// the step claim names over the partition key of
    /// <c>notification_attempt</c>. The ingestion caps a notification's TTL at
    /// thirty days and no attempt is queued after the plan ends, so twice that
    /// bound holds every row of every step while still letting the planner
    /// discard every partition the notification cannot possibly have rows in.
    /// Without the window the claim reads every month the table holds.
    /// </summary>
    internal static readonly TimeSpan AttemptWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// Claims the advance of the step the given channel occupies, stamping
    /// every attempt of that step. True means this caller owns the advance;
    /// false means another trigger already bought it and this one has nothing
    /// left to do.
    /// </summary>
    internal static async Task<bool> TryClaimStepAdvanceAsync(
        NotificationsDbContext db,
        Notification notification,
        string failedChannel,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset windowStart = notification.CreatedAt;
        DateTimeOffset windowEnd = notification.CreatedAt + AttemptWindow;
        var claimed = await db.NotificationAttempts
            .Where(candidate => candidate.NotificationId == notification.Id
                && candidate.Channel == failedChannel
                && candidate.PlanAdvancedAt == null
                && candidate.CreatedAt >= windowStart
                && candidate.CreatedAt < windowEnd)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.PlanAdvancedAt, now),
                cancellationToken);
        return claimed > 0;
    }

    /// <summary>
    /// Whether this verdict exhausted the current plan step: always for a
    /// single-target channel; for push, only when no sibling is still alive and
    /// none succeeded. A sibling queued, sending or unknown keeps the step
    /// open. A bounce counts exactly like a failure here: the destination
    /// refused the message, so that sibling will never deliver either.
    /// </summary>
    internal static async Task<bool> IsStepExhaustedAsync(
        NotificationsDbContext db,
        NotificationAttempt attempt,
        CancellationToken cancellationToken)
    {
        // Channel decides, never the in-memory token id: the claim stamps the
        // token through a guarded UPDATE the change tracker does not see.
        if (!string.Equals(attempt.Channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal)) return true;

        List<string> siblingStatuses = await db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.NotificationId == attempt.NotificationId
                && candidate.Channel == attempt.Channel
                && candidate.Id != attempt.Id)
            .Select(candidate => candidate.Status)
            .ToListAsync(cancellationToken);
        return siblingStatuses.All(status
            => status is NotificationAttemptStatuses.Failed or NotificationAttemptStatuses.Bounced);
    }

    /// <summary>
    /// Advances the plan inside the caller's transaction: a step with a
    /// fallback deadline asks the Core for the next one; the last step (null
    /// deadline) exhausts the plan and fails the notification.
    /// </summary>
    internal static async Task AdvanceAsync(
        PlanOutcomeScope scope,
        Notification notification,
        NotificationAttempt attempt,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.FallbackDeadline is not null)
        {
            await scope.OutboxWriter.AppendAsync(
                scope.Transaction,
                DispatchMessages.BuildFallbackRequested(
                    notification.RecipientId,
                    notification.Class,
                    notification.AuthFlow,
                    notification.Id,
                    attempt.Id,
                    now,
                    Activity.Current?.Id),
                cancellationToken);
            await scope.AuditTrail.AppendAsync(
                scope.Transaction,
                BuildAuditEntry(
                    scope.Actor,
                    DispatchingAuditVocabulary.FallbackTriggered,
                    notification,
                    new
                    {
                        failedAttemptId = attempt.Id,
                        channel = attempt.Channel,
                        reason,
                    },
                    now),
                cancellationToken);
            return;
        }

        if (notification.Status != NotificationStatuses.Dispatched)
        {
            // Another conclusion of the same notification committed first.
            // Two sources settle this state now, so a lost race is an ordinary
            // outcome and never an error: the notification already carries a
            // terminal state and a second terminal event would be a lie.
            return;
        }

        notification.MarkFailedAfterDispatch();
        await scope.Db.SaveChangesAsync(cancellationToken);

        // Before the audit append on purpose: the append takes the partition
        // chain lock and holds it until the transaction ends, so every write
        // queued after it stretches the window concurrent ingestion waits on.
        await scope.OutboxWriter.AppendAsync(
            scope.Transaction,
            NotificationEvents.Failed(new NotificationFailed
            {
                RecipientId = notification.RecipientId,
                Class = notification.Class,
                NotificationId = notification.Id,
                Reason = reason,
                LastChannel = attempt.Channel,
                CorrelationId = notification.CorrelationId,
                OccurredAt = now,
                Traceparent = Activity.Current?.Id,
            }),
            cancellationToken);
        await scope.AuditTrail.AppendAsync(
            scope.Transaction,
            BuildAuditEntry(
                scope.Actor,
                DispatchingAuditVocabulary.NotificationFailed,
                notification,
                new
                {
                    failedAttemptId = attempt.Id,
                    channel = attempt.Channel,
                    reason,
                },
                now),
            cancellationToken);
    }

    /// <summary>
    /// Ends the notification on delivered inside the caller's transaction and
    /// announces the result. A notification that already left the dispatched
    /// state writes nothing: only the first confirmation concludes.
    /// </summary>
    internal static async Task ConcludeDeliveredAsync(
        PlanOutcomeScope scope,
        Notification notification,
        NotificationAttempt attempt,
        DateTimeOffset deliveredAt,
        object auditDetails,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (notification.Status != NotificationStatuses.Dispatched) return;

        notification.MarkDelivered();
        await scope.Db.SaveChangesAsync(cancellationToken);

        // Before the audit append on purpose, for the same reason the failure
        // path writes in this order: the append holds the chain lock of the
        // trail's partition until the transaction ends.
        await scope.OutboxWriter.AppendAsync(
            scope.Transaction,
            NotificationEvents.Delivered(new NotificationDelivered
            {
                RecipientId = notification.RecipientId,
                Class = notification.Class,
                NotificationId = notification.Id,
                Channel = attempt.Channel,
                DeliveredAt = deliveredAt,
                CorrelationId = notification.CorrelationId,
                Traceparent = Activity.Current?.Id,
            }),
            cancellationToken);
        await scope.AuditTrail.AppendAsync(
            scope.Transaction,
            BuildAuditEntry(
                scope.Actor,
                DispatchingAuditVocabulary.NotificationDelivered,
                notification,
                auditDetails,
                now),
            cancellationToken);
    }

    private static AuditEntry BuildAuditEntry(
        PlanOutcomeActor actor,
        string action,
        Notification notification,
        object details,
        DateTimeOffset now)
        => new()
        {
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            Application = notification.Application,
            Action = action,
            EntityType = DispatchingAuditVocabulary.EntityTypeNotification,
            DetailsJson = JsonSerializer.Serialize(details),
            EntityId = notification.Id.ToString(),
            OccurredAt = now,
        };
}
