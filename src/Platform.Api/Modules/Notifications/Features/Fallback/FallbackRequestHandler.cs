using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Fallback;

/// <summary>
/// Handles one fallback trigger inside the Core role: verifies the TTL,
/// determines the next step of the published delivery plan, renders the next
/// channel's content and queues the next attempt with the same transactional
/// invariant as the pipeline commit: attempt, outbox message, audit event
/// and dedupe mark in one database transaction or not at all. TTL expiry
/// ends the notification on expired; a plan without a next usable step ends
/// it on failed.
/// <para>
/// The handler is the single point where the triggers of one step meet, so it
/// is where the step is claimed. Whatever produced the trigger, only the
/// transaction that wins the claim queues the next attempt.
/// </para>
/// </summary>
internal sealed class FallbackRequestHandler(
    NotificationsDbContext db,
    IProcessedMessageStore processedMessages,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    IPublishedCatalog catalog,
    IPublishedTemplateRenderer renderer,
    IRecipientDirectory recipientDirectory,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider,
    ILogger<FallbackRequestHandler> logger)
{
    internal const string ReasonPayloadWithoutIds = "payload-missing-fallback-reference";
    internal const string ReasonNotificationNotFound = "notification-not-found";
    internal const string ReasonFailedAttemptNotFound = "failed-attempt-not-found";

    /// <summary>Stable reason of a plan whose failed channel has no later usable step.</summary>
    internal const string ReasonPlanExhausted = "plan-exhausted";

    /// <summary>Stable reason of a next step without a reachable contact.</summary>
    internal const string ReasonNoValidContact = "no-valid-contact";

    /// <summary>Stable reason of a next step whose content no longer renders.</summary>
    internal const string ReasonRenderFailed = "template-render-failed";

    /// <summary>
    /// Refusal the published renderer answers with when the SMS render of an
    /// authentication template produces a link. It arrives here as the whole
    /// error text of a failed render, exactly as it does on the ingestion path,
    /// and it keeps its own reason for the same motive: the fallback step of an
    /// authentication plan is precisely where an SMS is reached, so folding it
    /// into a render failure would file a security refusal as a broken
    /// template and hide the only case the rule exists for.
    /// </summary>
    internal const string ReasonAuthenticationSmsLink = RenderStage.ReasonAuthenticationSmsLink;

    public async Task<MessageDisposition> ProcessAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!TryReadGuid(envelope.Payload, "notificationId", out Guid notificationId)
            || !TryReadGuid(envelope.Payload, "failedAttemptId", out Guid failedAttemptId))
        {
            return new MessageDisposition.Discard(ReasonPayloadWithoutIds);
        }

        Notification? notification = await db.Notifications
            .FirstOrDefaultAsync(candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null) return new MessageDisposition.Discard(ReasonNotificationNotFound);

        NotificationAttempt? failedAttempt = await db.NotificationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == failedAttemptId, cancellationToken);
        if (failedAttempt is null) return new MessageDisposition.Discard(ReasonFailedAttemptNotFound);

        if (notification.Status != NotificationStatuses.Dispatched)
        {
            // The notification already ended: a redelivered or superseded
            // trigger changes nothing and leaves only its duplicate trail.
            await RecordDuplicateAsync(notification, cancellationToken);
            logger.FallbackDuplicateSkipped(notification.Id, notification.Status);
            return new MessageDisposition.Duplicate();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (notification.ExpiresAt <= now)
        {
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Expired, reason: "expired", now, cancellationToken);
        }

        Result<PublishedClassPolicy> policy = await catalog.FindClassPolicyAsync(
            notification.Application, notification.Class, cancellationToken);
        if (policy.IsFailure)
        {
            // A missing published policy is an operational failure, never a
            // rejection: the message returns with backoff.
            throw new InvalidOperationException(policy.Error);
        }

        DeliveryPlanStep? nextStep = NextStep(policy.Value!.Definition.DeliveryPlan, failedAttempt.Channel);
        if (nextStep is null)
        {
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Failed, reason: ReasonPlanExhausted, now, cancellationToken);
        }

        Result<PublishedTemplateLookup> lookup = await catalog.FindTemplateAsync(
            notification.Application, notification.TemplateKey, cancellationToken);
        if (lookup.IsFailure || lookup.Value is PublishedTemplateLookup.Rejected)
        {
            var catalogReason = lookup.Value is PublishedTemplateLookup.Rejected rejected
                ? rejected.Reason
                : ReasonRenderFailed;
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Failed, reason: catalogReason, now, cancellationToken);
        }

        PublishedTemplate template = ((PublishedTemplateLookup.Published)lookup.Value!).Template;
        RecipientReadFallback readFallback = notification.Class == NotificationClasses.Critical
            || string.Equals(template.Purpose, ResolveStage.AuthenticationPurpose, StringComparison.Ordinal)
                ? RecipientReadFallback.LastKnown
                : RecipientReadFallback.None;
        Result<RecipientSnapshot> recipient = await recipientDirectory.FindAsync(
            notification.RecipientId, readFallback, cancellationToken);
        if (recipient.IsFailure)
        {
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Failed, reason: ReasonNoValidContact, now, cancellationToken);
        }

        var channel = nextStep.Channel.Value;
        Guid? contactPointId = null;
        if (!string.Equals(channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal))
        {
            contactPointId = recipient.Value!.ContactPoints
                .Where(point => string.Equals(point.Channel, channel, StringComparison.Ordinal))
                .OrderByDescending(point => point.Verified)
                .ThenBy(point => point.ContactPointId)
                .Select(point => (Guid?)point.ContactPointId)
                .FirstOrDefault();
            if (contactPointId is null)
            {
                return await SettleTerminalAsync(
                    envelope, notification, failedAttempt,
                    terminal: PipelineResult.Failed, reason: ReasonNoValidContact, now, cancellationToken);
            }
        }

        JsonElement? variables = await DecryptVariablesAsync(notification, cancellationToken);
        Result<PublishedTemplateRender> render = await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = notification.Application,
                TemplateKey = notification.TemplateKey,
                Channel = channel,
                Locale = recipient.Value!.Locale ?? template.DefaultLocale ?? RenderStage.FallbackLocale,
                Variables = variables,
                IncludeMaskedForm = true,
            },
            cancellationToken);
        if (render.IsFailure)
        {
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Failed, reason: RenderFailureReason(render.Error),
                now, cancellationToken);
        }

        return await QueueNextAttemptAsync(
            envelope, notification, failedAttempt, nextStep, template, contactPointId,
            render.Value!, now, cancellationToken);
    }

    /// <summary>
    /// Why the content of the next step never rendered. The security refusal
    /// keeps its own reason and everything else is a render failure, the same
    /// distinction the ingestion path draws, because the two ask different
    /// things of whoever reads the ended notification: one is a template to
    /// fix, the other is a rule that worked.
    /// </summary>
    internal static string RenderFailureReason(string? error)
        => string.Equals(error, ReasonAuthenticationSmsLink, StringComparison.Ordinal)
            ? ReasonAuthenticationSmsLink
            : ReasonRenderFailed;

    /// <summary>The step after the failed channel in the published plan; null when none follows.</summary>
    internal static DeliveryPlanStep? NextStep(IReadOnlyList<DeliveryPlanStep> plan, string failedChannel)
    {
        for (var index = 0; index < plan.Count - 1; index++)
            if (string.Equals(plan[index].Channel.Value, failedChannel, StringComparison.Ordinal)) return plan[index + 1];

        return null;
    }

    private enum PipelineResult
    {
        Expired,
        Failed,
    }

    private async Task<MessageDisposition> QueueNextAttemptAsync(
        MessageEnvelope envelope,
        Notification notification,
        NotificationAttempt failedAttempt,
        DeliveryPlanStep nextStep,
        PublishedTemplate template,
        Guid? contactPointId,
        PublishedTemplateRender render,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sealedContent = await RenderedContentEnvelope.SealAsync(
            cipher, notification.Application, render, cancellationToken);
        var channel = nextStep.Channel.Value;
        var destination = RouteStage.DestinationFor(template.Purpose, channel, notification.Class);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            PipelineCommitWriter.DedupeMessageId(envelope.MessageId, notification.Id),
            PipelineCommitWriter.ConsumerName,
            cancellationToken);
        if (!marked) return new MessageDisposition.Duplicate();

        if (!await NotificationPlanOutcome.TryClaimStepAdvanceAsync(
                db, notification, failedAttempt.Channel, now, cancellationToken))
        {
            // Another trigger already bought the advance of this step. The
            // reactive trigger and the deadline trigger are two queue rows with
            // two message identities, so the dedupe mark of each one passes and
            // only this claim can tell them apart; letting both through would
            // queue the same next step twice, which the recipient reads as two
            // messages.
            await transaction.RollbackAsync(cancellationToken);
            logger.FallbackStepAlreadyAdvanced(notification.Id, failedAttempt.Channel);
            return new MessageDisposition.Duplicate();
        }

        var nextSequence = await db.NotificationAttempts
            .Where(candidate => candidate.NotificationId == notification.Id)
            .MaxAsync(candidate => candidate.Sequence, cancellationToken) + 1;
        var attempt = NotificationAttempt.Queue(new NotificationAttemptDraft
        {
            NotificationId = notification.Id,
            Sequence = nextSequence,
            Channel = channel,
            ContactPointId = contactPointId,
            RenderedContentEncrypted = sealedContent,
            ContentHashFull = render.Full.ContentHash,
            ContentHashMasked = (render.Masked ?? render.Full).ContentHash,
            FallbackTimeout = nextStep.Timeout,
            QueuedAt = now,
        });
        db.NotificationAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
            DispatchMessages.BuildAttemptQueued(
                destination,
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
                DispatchingAuditVocabulary.FallbackAttemptQueued,
                notification,
                new
                {
                    failedAttemptId = failedAttempt.Id,
                    attemptId = attempt.Id,
                    channel,
                    sequence = attempt.Sequence,
                    destination,
                },
                now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.FallbackAttemptQueued(notification.Id, attempt.Id, channel);
        return new MessageDisposition.Processed();
    }

    /// <summary>
    /// Ends the notification in one transaction: transition, audit event and
    /// dedupe mark together. Expiry purges the encrypted variables, because
    /// no render will ever run again.
    /// </summary>
    private async Task<MessageDisposition> SettleTerminalAsync(
        MessageEnvelope envelope,
        Notification notification,
        NotificationAttempt failedAttempt,
        PipelineResult terminal,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            PipelineCommitWriter.DedupeMessageId(envelope.MessageId, notification.Id),
            PipelineCommitWriter.ConsumerName,
            cancellationToken);
        if (!marked) return new MessageDisposition.Duplicate();

        string action;
        if (terminal == PipelineResult.Expired)
        {
            notification.MarkExpiredAfterDispatch();
            action = PipelineAuditVocabulary.NotificationExpired;
        }
        else
        {
            notification.MarkFailedAfterDispatch();
            action = DispatchingAuditVocabulary.NotificationFailed;
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            BuildAuditEntry(
                action,
                notification,
                new { failedAttemptId = failedAttempt.Id, channel = failedAttempt.Channel, reason },
                now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.FallbackEndedNotification(notification.Id, notification.Status, reason);
        return new MessageDisposition.Processed();
    }

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
                    source = "fallback",
                    status = notification.Status,
                }),
                OccurredAt = timeProvider.GetUtcNow(),
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<JsonElement?> DecryptVariablesAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        if (notification.VariablesEncrypted is not { Length: > 0 } sealedVariables) return null;

        var plaintext = await cipher.DecryptAsync(
            notification.Application, sealedVariables, cancellationToken);
        using var document = JsonDocument.Parse(plaintext);
        return document.RootElement.Clone();
    }

    private static AuditEntry BuildAuditEntry(
        string action,
        Notification notification,
        object details,
        DateTimeOffset now)
        => new()
        {
            ActorType = PipelineAuditVocabulary.ActorTypeSystem,
            ActorId = PipelineAuditVocabulary.ActorIdCoreWorker,
            Application = notification.Application,
            Action = action,
            EntityType = PipelineAuditVocabulary.EntityTypeNotification,
            EntityId = notification.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
            OccurredAt = now,
        };

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = default;
        return payload.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value);
    }
}
