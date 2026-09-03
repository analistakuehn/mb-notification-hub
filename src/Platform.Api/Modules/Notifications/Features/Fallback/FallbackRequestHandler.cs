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
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
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

    /// <summary>
    /// Stable reason of a trigger whose attempt belongs to another
    /// notification. The two identifiers travel in the same payload and are
    /// resolved independently, so a malformed or crossed producer could pair
    /// one notification's policy with another notification's channel. The pair
    /// is rejected before any write: an inconsistent trigger settles nothing.
    /// </summary>
    internal const string ReasonAttemptNotificationMismatch = "attempt-notification-mismatch";

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

        if (failedAttempt.NotificationId != notification.Id)
        {
            // Both identifiers are resolved independently, so the pair has to
            // be checked: advancing here would apply this notification's plan
            // to another notification's channel and cross their audit trails.
            logger.FallbackAttemptNotificationMismatch(
                notification.Id, failedAttempt.Id, failedAttempt.NotificationId);
            return new MessageDisposition.Discard(ReasonAttemptNotificationMismatch);
        }

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

        // The plan this notification was admitted under, not the one published
        // right now: a republication is how a plan is activated and rolled
        // back, so re-deriving here would change the plan of a notification
        // already in flight. A row written before the column existed carries
        // none, and falls back to the published plan.
        AdmittedPlanRead admitted = AdmittedDeliveryPlan.Read(notification.AdmittedPlanJson);
        if (admitted is AdmittedPlanRead.Unreadable unreadable)
        {
            // A row older than the column and a document that no longer reads
            // both continue on the published plan, and only one of them is an
            // anomaly. The trail names the refused word, because continuing
            // here is exactly the case the stored plan exists to prevent: the
            // published order may name a channel the admission removed.
            logger.FallbackAdmittedPlanUnreadable(notification.Id, unreadable.Refused);
        }

        IReadOnlyList<DeliveryPlanStep> plan = PlanFor(admitted, policy.Value!.Definition.DeliveryPlan);

        // Whether any later step exists at all is answered before the catalog
        // and the directory are asked, because a plan whose failed channel was
        // the last one ends here and that is the ordinary case for a one step
        // plan. Which of the later steps is usable needs the recipient, so it
        // is decided below.
        if (!HasLaterStep(plan, failedAttempt.Channel))
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
            || TemplatePurposes.IsAuthentication(template.Purpose)
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

        // Eligibility is read now, never frozen with the plan. A destination
        // that died between the admission and this deadline must not be
        // addressed, and a channel the recipient withdrew consent for in the
        // meantime is no longer ours to use. An ineligible step is skipped
        // rather than fatal, which is what the admission does with the same
        // evidence: it filters the plan and keeps going.
        (DeliveryPlanStep? nextStep, var blockedReason) = NextUsableStep(
            plan, failedAttempt.Channel, recipient.Value!,
            policy.Value!.Definition.ConsentPurpose, now);
        if (nextStep is null)
        {
            logger.FallbackPlanStepBlocked(notification.Id, blockedReason!);
            return await SettleTerminalAsync(
                envelope, notification, failedAttempt,
                terminal: PipelineResult.Failed, reason: blockedReason!, now, cancellationToken);
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
    /// Why the content of the next step never rendered. The refusals the
    /// renderer words for itself keep their own reasons and everything else is
    /// a render failure, decided by the same table the ingestion path decides
    /// it with: each case asks something different of whoever reads the ended
    /// notification, one being a template to fix and the others rules that
    /// worked, and a second copy of the table would end up answering one of
    /// them differently here.
    /// </summary>
    internal static string RenderFailureReason(string? error)
        => RenderStage.ReasonForFailedRender(error);

    /// <summary>
    /// The plan this fallback decision runs on: the admitted one whenever the
    /// notification carries one that still reads, the published one otherwise.
    /// <para>
    /// Absence and an unreadable document resolve to the same plan and are not
    /// the same event. Absence is the ordinary history of a row older than the
    /// column. An unreadable document continues on the published order, which
    /// may name a channel the admission had already removed, and that is the
    /// case the stored plan exists to prevent, so the caller leaves a witness
    /// for one and not for the other.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DeliveryPlanStep> PlanFor(
        AdmittedPlanRead admitted,
        IReadOnlyList<DeliveryPlanStep> published)
        => admitted is AdmittedPlanRead.Present present ? present.Plan : published;

    /// <summary>The step after the failed channel in the plan; null when none follows.</summary>
    internal static DeliveryPlanStep? NextStep(IReadOnlyList<DeliveryPlanStep> plan, string failedChannel)
    {
        for (var index = 0; index < plan.Count - 1; index++)
            if (string.Equals(plan[index].Channel.Value, failedChannel, StringComparison.Ordinal)) return plan[index + 1];

        return null;
    }

    /// <summary>Whether the plan names any step after the failed channel.</summary>
    internal static bool HasLaterStep(IReadOnlyList<DeliveryPlanStep> plan, string failedChannel)
        => NextStep(plan, failedChannel) is not null;

    /// <summary>
    /// The first step after the failed channel the recipient may still be
    /// addressed on, or the reason the plan ran out of usable ones.
    /// <para>
    /// The search walks forward instead of stopping at the immediate next step,
    /// because a channel that became ineligible is not a reason to end a
    /// notification that still has a usable channel behind it. When nothing is
    /// usable the reason reported is the one that blocked the first later step,
    /// which is the channel the plan would have taken.
    /// </para>
    /// </summary>
    internal static (DeliveryPlanStep? Step, string? BlockedReason) NextUsableStep(
        IReadOnlyList<DeliveryPlanStep> plan,
        string failedChannel,
        RecipientSnapshot recipient,
        string? consentPurpose,
        DateTimeOffset now)
    {
        var failedIndex = -1;
        for (var index = 0; index < plan.Count; index++)
        {
            if (string.Equals(plan[index].Channel.Value, failedChannel, StringComparison.Ordinal))
            {
                failedIndex = index;
                break;
            }
        }

        if (failedIndex < 0)
        {
            return (null, ReasonPlanExhausted);
        }

        string? firstBlocked = null;
        for (var index = failedIndex + 1; index < plan.Count; index++)
        {
            var candidate = plan[index].Channel.Value;
            var blocked = !HasConsentFor(recipient, consentPurpose, candidate)
                ? ConsentGateRule.ReasonNoConsent
                : IsChannelSuppressed(recipient, candidate, now)
                    ? SuppressionGateRule.ReasonChannelSuppressed
                    : null;
            if (blocked is null)
            {
                return (plan[index], null);
            }

            firstBlocked ??= blocked;
        }

        return (null, firstBlocked ?? ReasonPlanExhausted);
    }

    /// <summary>
    /// Consent for one channel, read the same way the policy rule reads it: a
    /// class without a purpose operates on a contractual or legal basis and
    /// consults nothing, and a declared purpose is canonicalized before the
    /// comparison, because the class policy authors it in another module while
    /// the snapshot already carries the canonical key.
    /// </summary>
    private static bool HasConsentFor(RecipientSnapshot recipient, string? consentPurpose, string channel)
    {
        if (consentPurpose is not { Length: > 0 } declared)
        {
            return true;
        }

        var purpose = ConsentPurpose.Canonicalize(declared);
        return recipient.Consents.Any(consent => consent.Granted
            && string.Equals(consent.Purpose, purpose, StringComparison.Ordinal)
            && string.Equals(consent.Channel, channel, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the hub stopped addressing every destination the recipient has
    /// on one channel, read the same way the policy rule reads it. A recipient
    /// who keeps a second live address on the channel is still reachable there,
    /// and a channel the recipient has no contact point on is not suppressed,
    /// it is unreachable, which the caller answers for separately.
    /// </summary>
    private static bool IsChannelSuppressed(
        RecipientSnapshot recipient,
        string channel,
        DateTimeOffset now)
    {
        var suppressedPoints = recipient.Suppressions
            .Where(suppression => suppression.Until is null || suppression.Until > now)
            .Select(suppression => suppression.ContactPointId)
            .ToHashSet();
        List<ContactPointSnapshot> points = [.. recipient.ContactPoints
            .Where(point => string.Equals(point.Channel, channel, StringComparison.Ordinal))];
        return points.Count > 0
            && points.TrueForAll(point => suppressedPoints.Contains(point.ContactPointId));
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
        // The set the next attempt inherits, read off the notification this
        // handler already loaded and never carried in the trigger: the step
        // about to be queued departs from the same composition the first one
        // did, and that is what keeps every step of one plan on the set the
        // producer was told had been accepted. A document that no longer reads
        // stops the step before the advance is claimed, so the trigger that
        // follows the repair still finds a step to buy.
        AcceptedAttachmentManifest.RefuseUnreadable(notification);

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
                    failedStatus = failedAttempt.Status,
                    attemptId = attempt.Id,
                    channel,
                    sequence = attempt.Sequence,
                    destination,
                },
                now),
            cancellationToken);

        // A step that advanced from an inconclusive verdict is the one case
        // where this hub may reach the same person twice, and it says so.
        // The entry rides in the transaction that already holds the chain lock
        // of its partition, so it costs the append and no second lock. It
        // claims a risk taken, never a duplicate observed: the send it followed
        // was never answered, and nothing downstream will ever answer it.
        if (string.Equals(
            failedAttempt.Status, NotificationAttemptStatuses.Unknown, StringComparison.Ordinal))
        {
            await auditTrail.AppendAsync(
                transaction.GetDbTransaction(),
                BuildAuditEntry(
                    DispatchingAuditVocabulary.FallbackRequestedFromUnknown,
                    notification,
                    new
                    {
                        failedAttemptId = failedAttempt.Id,
                        failedChannel = failedAttempt.Channel,
                        attemptId = attempt.Id,
                        channel,
                        duplicateRiskAccepted = true,
                    },
                    now),
                cancellationToken);
            logger.FallbackRequestedFromUnknown(notification.Id, failedAttempt.Id, channel);
        }

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
