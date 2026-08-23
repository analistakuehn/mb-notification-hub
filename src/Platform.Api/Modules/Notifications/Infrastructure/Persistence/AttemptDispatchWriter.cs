using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Events;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>Outcome of one optimistic claim over a queued attempt.</summary>
internal enum AttemptClaimOutcome
{
    /// <summary>This dispatcher owns the attempt now; the send may run.</summary>
    Claimed = 0,

    /// <summary>
    /// The attempt was not queued anymore: a concurrent claim won, or a
    /// redelivery arrived after the verdict. The message is a duplicate.
    /// </summary>
    NotQueued = 1,
}

/// <summary>
/// Transactional writes of the dispatch side of the attempt state machine.
/// Every transition is an optimistic UPDATE guarded by the expected stored
/// status, so two concurrent claims of one attempt can never both send. The
/// consumer dedupe mark commits with the verdict, never with the claim: a
/// send is not idempotent, so a redelivery after a claim must resolve on the
/// stored status instead of ever reaching the provider again. The commit
/// always follows the audit append immediately, because the append holds the
/// partition chain lock until the transaction ends.
/// </summary>
/// <remarks>
/// A terminal verdict also settles the rendered content: in the same
/// statement that writes the verdict, the sealed envelope is rewritten with
/// its masked form alone, because the complete content loses its purpose the
/// instant the provider takes or refuses the message. A fallback step never
/// reuses the seal, it renders and seals its own, and an attempt parked on
/// unknown never resends either, so no path needs the complete form back.
/// </remarks>
internal sealed class AttemptDispatchWriter(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider)
{
    internal const string ConsumerName = "dispatcher";

    /// <summary>Canonical channel whose attempts fan out over sibling device tokens.</summary>
    internal const string PushChannel = "push";

    /// <summary>Stable code of a push attempt claimed with zero active device tokens.</summary>
    internal const string ErrorNoActiveDeviceToken = "no-active-device-token";

    /// <summary>How many device tokens one push notification fans out to, at most.</summary>
    internal const int PushFanOutLimit = 5;

    internal static string DedupeMessageId(Guid envelopeMessageId, Guid attemptId)
        => $"{envelopeMessageId:N}:{attemptId:N}";

    /// <summary>Claims one ready-to-send attempt: email, or a push sibling already carrying its token.</summary>
    public async Task<AttemptClaimOutcome> TryClaimAsync(
        NotificationAttempt attempt,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var claimed = await db.NotificationAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.Status == NotificationAttemptStatuses.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Sending)
                    .SetProperty(candidate => candidate.ProviderKey, providerKey),
                cancellationToken);
        return claimed == 1 ? AttemptClaimOutcome.Claimed : AttemptClaimOutcome.NotQueued;
    }

    /// <summary>
    /// Claims an unexpanded push attempt and expands the fan-out in the same
    /// transaction: the claimed attempt is stamped with the most recent
    /// token, and one sibling per remaining token is inserted already queued,
    /// copying the rendered content, the hashes and the step's absolute
    /// fallback deadline, each announced to the same dispatch queue.
    /// </summary>
    public async Task<AttemptClaimOutcome> TryClaimPushAsync(
        NotificationAttempt attempt,
        Notification notification,
        string providerKey,
        IReadOnlyList<Guid> deviceTokenIds,
        string sourceQueue,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(deviceTokenIds.Count);
        DateTimeOffset now = timeProvider.GetUtcNow();

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        Guid stampedToken = deviceTokenIds[0];
        var claimed = await db.NotificationAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.Status == NotificationAttemptStatuses.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Sending)
                    .SetProperty(candidate => candidate.ProviderKey, providerKey)
                    .SetProperty(candidate => candidate.DeviceTokenId, stampedToken),
                cancellationToken);
        if (claimed == 0)
        {
            return AttemptClaimOutcome.NotQueued;
        }

        var nextSequence = await db.NotificationAttempts
            .Where(candidate => candidate.NotificationId == notification.Id)
            .MaxAsync(candidate => candidate.Sequence, cancellationToken) + 1;
        foreach (Guid tokenId in deviceTokenIds.Skip(1))
        {
            var sibling = NotificationAttempt.Queue(new NotificationAttemptDraft
            {
                NotificationId = notification.Id,
                Sequence = nextSequence++,
                Channel = attempt.Channel,
                ContactPointId = null,
                DeviceTokenId = tokenId,
                RenderedContentEncrypted = attempt.RenderedContentEncrypted,
                ContentHashFull = attempt.ContentHashFull,
                ContentHashMasked = attempt.ContentHashMasked,
                FallbackDeadline = attempt.FallbackDeadline,
                QueuedAt = now,
            });
            db.NotificationAttempts.Add(sibling);
            await db.SaveChangesAsync(cancellationToken);
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(),
                DispatchMessages.BuildAttemptQueued(
                    sourceQueue,
                    notification.RecipientId,
                    notification.Class,
                    notification.Id,
                    sibling.Id,
                    now,
                    Activity.Current?.Id),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return AttemptClaimOutcome.Claimed;
    }

    /// <summary>
    /// Settles a push attempt claimed with zero active device tokens: the
    /// attempt fails with a stable code and the plan advances in the same
    /// transaction, exactly like a provider rejection would advance it.
    /// </summary>
    public async Task<bool> TryFailWithoutTokensAsync(
        NotificationAttempt attempt,
        Notification notification,
        Guid envelopeMessageId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var durableContent = await DurableContentAsync(attempt, notification, cancellationToken);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DedupeMessageId(envelopeMessageId, attempt.Id),
            ConsumerName,
            cancellationToken);
        if (!marked)
        {
            return false;
        }

        var failed = await db.NotificationAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.Status == NotificationAttemptStatuses.Queued)
            .ExecuteUpdateAsync(
                setters =>
                {
                    setters
                        .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Failed)
                        .SetProperty(candidate => candidate.ErrorCode, ErrorNoActiveDeviceToken);
                    DiscardCompleteForm(setters, durableContent);
                },
                cancellationToken);
        if (failed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await AdvancePlanAsync(
            transaction, notification, attempt, ErrorNoActiveDeviceToken, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Records the provider acceptance: the attempt reaches sent, and a push
    /// notification whose first sibling just succeeded reaches delivered,
    /// because acceptance by the push provider is that channel's delivery
    /// signal. Only the first success transitions the notification.
    /// </summary>
    public async Task<bool> RecordSentAsync(
        NotificationAttempt attempt,
        Notification notification,
        string providerKey,
        string? providerMessageId,
        Guid envelopeMessageId,
        bool deliveredOnAcceptance,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var durableContent = await DurableContentAsync(attempt, notification, cancellationToken);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DedupeMessageId(envelopeMessageId, attempt.Id),
            ConsumerName,
            cancellationToken);
        if (!marked)
        {
            return false;
        }

        await TransitionFromSendingAsync(
            attempt.Id,
            durableContent,
            setters => setters
                .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Sent)
                .SetProperty(candidate => candidate.ProviderMessageId, providerMessageId)
                .SetProperty(candidate => candidate.SentAt, now),
            cancellationToken);

        if (deliveredOnAcceptance && notification.Status == NotificationStatuses.Dispatched)
        {
            notification.MarkDelivered();
            await db.SaveChangesAsync(cancellationToken);

            // Before the audit append on purpose: the append takes the
            // partition chain lock and holds it until the transaction ends, so
            // every write queued after it stretches the window concurrent
            // ingestion waits on.
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(),
                NotificationEvents.Delivered(new NotificationDelivered
                {
                    RecipientId = notification.RecipientId,
                    Class = notification.Class,
                    NotificationId = notification.Id,
                    Channel = attempt.Channel,
                    DeliveredAt = now,
                    CorrelationId = notification.CorrelationId,
                    Traceparent = Activity.Current?.Id,
                }),
                cancellationToken);
            await auditTrail.AppendAsync(
                transaction.GetDbTransaction(),
                BuildAuditEntry(
                    DispatchingAuditVocabulary.NotificationDelivered,
                    notification,
                    new
                    {
                        attemptId = attempt.Id,
                        channel = attempt.Channel,
                        providerKey,
                        providerMessageId,
                    },
                    now),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Records a definitive failure and advances the plan in the same
    /// transaction when this failure exhausts the current step: immediately
    /// for a single-target channel, and for push only when every sibling is
    /// already failed and none succeeded.
    /// </summary>
    public async Task<bool> RecordFailureAsync(
        NotificationAttempt attempt,
        Notification notification,
        string errorCode,
        Guid envelopeMessageId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var durableContent = await DurableContentAsync(attempt, notification, cancellationToken);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DedupeMessageId(envelopeMessageId, attempt.Id),
            ConsumerName,
            cancellationToken);
        if (!marked)
        {
            return false;
        }

        await TransitionFromSendingAsync(
            attempt.Id,
            durableContent,
            setters => setters
                .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Failed)
                .SetProperty(candidate => candidate.ErrorCode, errorCode),
            cancellationToken);

        if (await StepExhaustedAsync(attempt, notification, cancellationToken))
        {
            await AdvancePlanAsync(transaction, notification, attempt, errorCode, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Returns the attempt to the queue without a verdict: throttling or an
    /// open circuit proves the provider did not take the call, so the message
    /// may come back later and claim again. No dedupe mark on purpose: the
    /// redelivery must reprocess.
    /// </summary>
    public async Task RevertToQueuedAsync(NotificationAttempt attempt, CancellationToken cancellationToken)
        => await TransitionFromSendingAsync(
            attempt.Id,
            durableContent: null,
            setters => setters
                .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Queued)
                .SetProperty(candidate => candidate.ProviderKey, (string?)null),
            cancellationToken);

    /// <summary>
    /// Records the absence of a conclusive verdict: the attempt parks on
    /// unknown and nothing progresses, because whether the message arrived is
    /// unknown and only reconciliation may settle it. The rendered content
    /// still settles here: reconciliation asks the provider by message id and
    /// never resends content, so the complete form has no reader left.
    /// </summary>
    public async Task<bool> RecordUnknownAsync(
        NotificationAttempt attempt,
        Notification notification,
        string? errorCode,
        Guid envelopeMessageId,
        CancellationToken cancellationToken)
    {
        var durableContent = await DurableContentAsync(attempt, notification, cancellationToken);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            DedupeMessageId(envelopeMessageId, attempt.Id),
            ConsumerName,
            cancellationToken);
        if (!marked)
        {
            return false;
        }

        await TransitionFromSendingAsync(
            attempt.Id,
            durableContent,
            setters => setters
                .SetProperty(candidate => candidate.Status, NotificationAttemptStatuses.Unknown)
                .SetProperty(candidate => candidate.ErrorCode, errorCode),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Whether this failure exhausted the current plan step: always for a
    /// single-target channel; for push, only when no sibling succeeded and
    /// every other sibling already failed. A sibling still queued, sending or
    /// unknown keeps the step open.
    /// </summary>
    private async Task<bool> StepExhaustedAsync(
        NotificationAttempt attempt,
        Notification notification,
        CancellationToken cancellationToken)
    {
        // Channel decides, never the in-memory token id: the claim stamps the
        // token through a guarded UPDATE the change tracker does not see.
        if (!string.Equals(attempt.Channel, PushChannel, StringComparison.Ordinal))
        {
            return true;
        }

        List<string> siblingStatuses = await db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.NotificationId == notification.Id
                && candidate.Channel == attempt.Channel
                && candidate.Id != attempt.Id)
            .Select(candidate => candidate.Status)
            .ToListAsync(cancellationToken);
        return siblingStatuses.All(status => status == NotificationAttemptStatuses.Failed);
    }

    /// <summary>
    /// Advances the plan inside the caller's transaction: a step with a
    /// fallback deadline asks the Core for the next one; the last step (null
    /// deadline) exhausts the plan and fails the notification.
    /// </summary>
    private async Task AdvancePlanAsync(
        IDbContextTransaction transaction,
        Notification notification,
        NotificationAttempt attempt,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.FallbackDeadline is not null)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(),
                DispatchMessages.BuildFallbackRequested(
                    notification.RecipientId,
                    notification.Class,
                    notification.Id,
                    attempt.Id,
                    now,
                    Activity.Current?.Id),
                cancellationToken);
            await auditTrail.AppendAsync(
                transaction.GetDbTransaction(),
                BuildAuditEntry(
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

        notification.MarkFailedAfterDispatch();
        await db.SaveChangesAsync(cancellationToken);

        // Before the audit append on purpose: the append takes the partition
        // chain lock and holds it until the transaction ends, so every write
        // queued after it stretches the window concurrent ingestion waits on.
        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
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
        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            BuildAuditEntry(
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
    /// Applies one guarded transition from sending, together with the durable
    /// rendered content when the verdict settles one. Zero affected rows is a
    /// defect, not a race: only the claim owner ever settles the attempt.
    /// </summary>
    private async Task TransitionFromSendingAsync(
        Guid attemptId,
        byte[]? durableContent,
        Action<UpdateSettersBuilder<NotificationAttempt>> setters,
        CancellationToken cancellationToken)
    {
        var affected = await db.NotificationAttempts
            .Where(candidate => candidate.Id == attemptId
                && candidate.Status == NotificationAttemptStatuses.Sending)
            .ExecuteUpdateAsync(
                builder =>
                {
                    setters(builder);
                    DiscardCompleteForm(builder, durableContent);
                },
                cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"O attempt {attemptId} não estava em 'sending' ao registrar o veredito; "
                + "somente o dono do claim liquida um attempt.");
        }
    }

    /// <summary>
    /// The envelope this attempt keeps after the verdict: the masked form
    /// alone. Null when the two forms coincide, and null when the stored
    /// envelope carries no masked form (an attempt sealed before the
    /// transition existed), so the row is left untouched in both cases and the
    /// sweep or the backfill decides what to do with it.
    /// </summary>
    private async Task<byte[]?> DurableContentAsync(
        NotificationAttempt attempt,
        Notification notification,
        CancellationToken cancellationToken)
        => string.Equals(attempt.ContentHashFull, attempt.ContentHashMasked, StringComparison.Ordinal)
            ? null
            : await RenderedContentEnvelope.TryDiscardCompleteFormAsync(
                cipher, notification.Application, attempt.RenderedContentEncrypted, cancellationToken);

    private static void DiscardCompleteForm(
        UpdateSettersBuilder<NotificationAttempt> setters,
        byte[]? durableContent)
    {
        if (durableContent is not null)
        {
            setters.SetProperty(candidate => candidate.RenderedContentEncrypted, durableContent);
        }
    }

    private static AuditEntry BuildAuditEntry(
        string action,
        Notification notification,
        object details,
        DateTimeOffset now)
        => new()
        {
            ActorType = DispatchingAuditVocabulary.ActorTypeSystem,
            ActorId = DispatchingAuditVocabulary.ActorIdDispatcher,
            Application = notification.Application,
            Action = action,
            EntityType = DispatchingAuditVocabulary.EntityTypeNotification,
            EntityId = notification.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
            OccurredAt = now,
        };
}
