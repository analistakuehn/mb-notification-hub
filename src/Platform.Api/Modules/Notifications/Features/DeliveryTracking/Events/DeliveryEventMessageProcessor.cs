using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// Consumer-side entry of the delivery-feedback queue: reads the claim check,
/// rebuilds the canonical event from the stored evidence and hands it to the
/// single applier of the feedback-driven state machine. The state work sits
/// here, off the request that received the callback, because that request
/// answers a provider that retries whatever takes too long, while this one can
/// take the locks it needs.
/// <para>
/// It is also where a refusal of the destination itself is reported to the
/// context that owns contacts, after the attempt transition is confirmed and
/// never before: a signal that did not move the attempt describes feedback this
/// hub could not place, and acting on it would suppress a contact on the
/// strength of a callback nobody could correlate.
/// </para>
/// </summary>
internal sealed class DeliveryEventMessageProcessor(
    NotificationsDbContext db,
    DeliveryStateApplier applier,
    ISuppressionLedger suppressionLedger,
    IOptions<DeliveryTrackingOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryEventMessageProcessor> logger) : ISqsMessageProcessor
{
    internal const int SupportedSchemaVersion = DeliveryTrackingMessages.SchemaVersion;
    internal const string ReasonPayloadWithoutId = "payload-missing-delivery-event-reference";
    internal const string ReasonEvidenceNotFound = "delivery-event-not-found";
    internal const string ReasonKindUnknown = "delivery-event-kind-unknown";
    internal const string ReasonAttemptUnresolved = "attempt-unresolved";

    public string Consumer => DeliveryStateApplier.ConsumerName;

    public bool Accepts(string type, int schemaVersion)
        => string.Equals(type, DeliveryTrackingMessages.EventReceivedType, StringComparison.Ordinal)
            && schemaVersion == SupportedSchemaVersion;

    public async Task<MessageDisposition> ProcessAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!TryReadGuid(envelope.Payload, "deliveryEventId", out Guid deliveryEventId)) return new MessageDisposition.Discard(ReasonPayloadWithoutId);

        DeliveryEvent? evidence = await db.DeliveryEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == deliveryEventId, cancellationToken);
        if (evidence is null)
        {
            // The outbox row commits with the evidence, so an absent row means
            // the claim check outlived its state: permanently unprocessable.
            return new MessageDisposition.Discard(ReasonEvidenceNotFound);
        }

        if (evidence.AppliedAt is not null)
        {
            logger.DeliveryEventAlreadyApplied(deliveryEventId);
            return new MessageDisposition.Duplicate();
        }

        if (!DeliveryEventKinds.TryParse(evidence.Kind, out DeliveryFeedbackKind kind))
        {
            logger.DeliveryEventKindUnknown(deliveryEventId, evidence.Kind);
            return new MessageDisposition.Discard(ReasonKindUnknown);
        }

        DeliveryApplicationOutcome outcome = await applier.ApplyAsync(
            new DeliveryApplicationRequest
            {
                Event = RebuildEvent(evidence, kind),
                DeliveryEventId = evidence.Id,
                DedupeMessageId = DeliveryStateApplier.DedupeMessageId(envelope.MessageId, evidence.Id),
            },
            cancellationToken);

        if (outcome is DeliveryApplicationOutcome.Applied)
        {
            await ReportSuppressionAsync(evidence, cancellationToken);
        }

        return outcome switch
        {
            DeliveryApplicationOutcome.Applied => new MessageDisposition.Processed(),

            // Feedback that changes nothing is a settled message, not a
            // failure: the evidence stays stored and unapplied, and the queue
            // has nothing left to do with it.
            DeliveryApplicationOutcome.Ignored => new MessageDisposition.Processed(),
            DeliveryApplicationOutcome.Duplicate => new MessageDisposition.Duplicate(),
            DeliveryApplicationOutcome.AttemptUnresolved => Unresolved(evidence),
            _ => throw new InvalidOperationException(
                $"Desfecho de aplicação de feedback não suportado: {outcome}."),
        };
    }

    /// <summary>
    /// Rebuilds the canonical event from the stored evidence, including the
    /// suppression signal exactly as the provider adapters classified it at
    /// ingestion. Nothing is reclassified here: the vocabulary of definitive
    /// failure codes is operator-configured provider knowledge, and a second
    /// reading of it on this side would be a second classifier free to drift.
    /// </summary>
    private static ProviderDeliveryEvent RebuildEvent(DeliveryEvent evidence, DeliveryFeedbackKind kind)
        => new(
            evidence.ProviderKey,
            evidence.ProviderEventId,
            kind,
            evidence.OccurredAt,
            evidence.ProviderMessageId,
            evidence.AttemptId is { } attemptId && evidence.NotificationId is { } notificationId
                ? new DispatchCorrelation(notificationId, attemptId)
                : null,
            evidence.ErrorCode,
            DeliverySuppressionSignals.Parse(evidence.SuppressionSignal));

    /// <summary>
    /// Reports a refused destination to the context that owns contacts. Best
    /// effort by design, in the same regime as the dead push token: the attempt
    /// transition already committed and a redelivery settles as a duplicate, so
    /// a failure here cannot be retried by the queue, and the reconciliation of
    /// a later phase remains the safety net. What to do about the refusal is
    /// not decided here either: this side reports one observation and the
    /// ledger owns the accumulation rule.
    /// </summary>
    private async Task ReportSuppressionAsync(DeliveryEvent evidence, CancellationToken cancellationToken)
    {
        if (DeliverySuppressionSignals.Parse(evidence.SuppressionSignal) == SuppressionSignal.None) return;

        SuppressionTarget? target = await ResolveSuppressionTargetAsync(evidence, cancellationToken);
        if (target is null)
        {
            // A push attempt carries a device registration and no contact
            // point: a token the provider refuses travels the token lifecycle
            // contract, which the dispatch side already reports on.
            logger.SuppressionTargetUnresolved(evidence.Id, evidence.SuppressionSignal);
            return;
        }

        try
        {
            Result<SuppressionOutcome> reported = await suppressionLedger.ReportDeliveryFeedbackAsync(
                new SuppressionReport(
                    target.RecipientId,
                    target.ContactPointId,
                    target.Channel,
                    evidence.SuppressionSignal,
                    evidence.Id,

                    // This hub's instant, and never the provider's: the ledger
                    // accumulates refusals inside a window, and an instant the
                    // provider chooses could slide that window open from
                    // outside.
                    evidence.ReceivedAt),
                cancellationToken);
            if (reported.IsFailure)
            {
                logger.SuppressionReportFailed(evidence.Id, reported.Error ?? evidence.SuppressionSignal);
                return;
            }

            var settled = reported.Value.ToString();
            logger.SuppressionReported(evidence.Id, target.ContactPointId, settled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.SuppressionReportThrew(evidence.Id, exception);
        }
    }

    /// <summary>
    /// The contact point the refused attempt addressed. The evidence row was
    /// read before the application, so its correlation may still be empty here
    /// even though the application resolved and stamped one; the second read
    /// runs only in that case, and only for feedback that carries a signal at
    /// all.
    /// </summary>
    private async Task<SuppressionTarget?> ResolveSuppressionTargetAsync(
        DeliveryEvent evidence,
        CancellationToken cancellationToken)
    {
        Guid? attemptId = evidence.AttemptId ?? await db.DeliveryEvents
            .AsNoTracking()
            .Where(candidate => candidate.Id == evidence.Id && candidate.ReceivedAt == evidence.ReceivedAt)
            .Select(candidate => candidate.AttemptId)
            .FirstOrDefaultAsync(cancellationToken);
        if (attemptId is not { } resolved) return null;

        return await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.Id == resolved && attempt.ContactPointId != null)
            .Join(
                db.Notifications.AsNoTracking(),
                attempt => attempt.NotificationId,
                notification => notification.Id,
                (attempt, notification) => new SuppressionTarget(
                    notification.RecipientId, attempt.ContactPointId!.Value, attempt.Channel))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Feedback that arrived before the send it describes. The message comes
    /// back for a bounded window and is then discarded with a record: an
    /// attempt that never appears will never appear, and a message that
    /// returns forever occupies the queue for its whole retention.
    /// </summary>
    private MessageDisposition Unresolved(DeliveryEvent evidence)
    {
        DeliveryTrackingOptions tuning = options.Value;
        TimeSpan age = timeProvider.GetUtcNow() - evidence.ReceivedAt;
        if (age >= tuning.UnresolvedAttemptWindow)
        {
            logger.DeliveryEventAbandoned(evidence.Id, tuning.UnresolvedAttemptWindow);
            return new MessageDisposition.Discard(ReasonAttemptUnresolved);
        }

        logger.DeliveryEventPostponed(evidence.Id, tuning.UnresolvedAttemptRetryDelay);
        return new MessageDisposition.Postponed(
            tuning.UnresolvedAttemptRetryDelay, ReasonAttemptUnresolved);
    }

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = default;
        return payload.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value);
    }
}

/// <summary>The contact point one refused attempt addressed, and who owns it.</summary>
internal sealed record SuppressionTarget(string RecipientId, Guid ContactPointId, string Channel);
