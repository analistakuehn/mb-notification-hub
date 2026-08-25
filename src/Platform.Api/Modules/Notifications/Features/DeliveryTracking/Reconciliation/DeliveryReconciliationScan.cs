using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>What one reconciliation round examined, asked and settled.</summary>
internal readonly record struct DeliveryReconciliationResult(
    int Examined,
    int Queried,
    int Corrected,
    int WithoutLookup,
    int Unanswered,
    int LiabilityRetired);

/// <summary>
/// One attempt this round may ask a provider about, with everything the
/// question is made of. The destination is deliberately absent: it is resolved
/// per attempt, at the moment of the call, and never carried in a set.
/// </summary>
internal sealed record ReconciliationCandidate(
    Guid AttemptId,
    Guid NotificationId,
    string RecipientId,
    string Channel,
    string Status,
    string ProviderKey,
    string? ProviderMessageId,
    Guid? ContactPointId,
    DateTimeOffset SentAt,
    DateTimeOffset ParkedSince);

/// <summary>
/// The correction of last resort: for every attempt the providers accepted or
/// left inconclusive and never reported on again, it asks the provider what
/// happened and records the answer as ordinary delivery feedback.
/// <para>
/// The answer enters through the very same door a callback enters: one
/// evidence row under the same provider event identity, and one application by
/// the same state applier. That is the whole design. A reconciliation with a
/// path of its own would be a second state machine over the same attempts,
/// free to conclude what the callback path would never conclude, and the
/// divergence would surface as attempts in states nobody can explain.
/// </para>
/// <para>
/// Deduplication is shared for the same reason. The identity of an event is
/// the provider's, so an event a callback already recorded is refused here and
/// an event recorded here is refused to a callback that arrives afterwards.
/// Without that, one refusal seen by both halves would count twice in the
/// contact ledger, and on a channel that suppresses at the second refusal that
/// arithmetic takes a reachable destination away from a person who was refused
/// once.
/// </para>
/// <para>
/// The lookup never feeds the channel circuit observer, and the omission is
/// deliberate rather than forgotten. That window measures how long sends have
/// been failing, and its consequence is stopping a channel for everybody. A
/// lookup is a read, made by a batch job, about a message that was sent hours
/// ago: counting its timeout as a send verdict would mean a bad minute for a
/// reporting API could stop a channel that is delivering perfectly. There is
/// no gap left by the omission either, because an attempt the rate limiter
/// barred never reached a provider and stays queued, outside the two statuses
/// this scan reads at all.
/// </para>
/// </summary>
internal sealed class DeliveryReconciliationScan(
    NotificationsDbContext db,
    IProviderDeliveryLookupResolver lookupResolver,
    IRecipientDirectory recipientDirectory,
    DeliveryEventWriter evidenceWriter,
    DeliveryStateApplier applier,
    ScanIndexLiabilitySweep liabilitySweep,
    IOptions<DeliveryReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryReconciliationScan> logger)
{
    public async Task<DeliveryReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        DeliveryReconciliationOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();

        var retired = await liabilitySweep.RunAsync(cancellationToken);
        IReadOnlyList<ReconciliationCandidate> candidates = await CandidatesAsync(
            now - settings.StaleAfter, settings.BatchSize, cancellationToken);

        var queried = 0;
        var corrected = 0;
        var withoutLookup = 0;
        foreach (ReconciliationCandidate candidate in candidates)
        {
            Result<IProviderDeliveryLookup> lookup = lookupResolver.Resolve(candidate.ProviderKey);
            if (lookup.IsFailure)
            {
                // A provider with no lookup is a statement, not an outage: the
                // attempt stays exactly as it is, with this record, and only a
                // fallback or the validity will ever settle it. The record is a
                // log line and not a trail entry on purpose: the same rows come
                // back every round for the life of the partition, and a trail
                // append takes the chain lock of its monthly partition, so one
                // append per unanswerable row per day would tax the ingestion
                // of the whole hub to restate a fact that never changes.
                withoutLookup++;
                logger.ReconciliationWithoutLookup(
                    candidate.AttemptId, candidate.ProviderKey, candidate.Status);
                continue;
            }

            queried++;

            // Attempts corrected, never answers applied: one answer can carry
            // several events about the same attempt, and the number an operator
            // reads has to mean rows whose record stopped being wrong.
            if (await ReconcileAsync(lookup.Value!, candidate, cancellationToken) > 0) corrected++;
        }

        var unanswered = candidates.Count - withoutLookup - corrected;
        if (candidates.Count > 0 || retired > 0)
        {
            logger.ReconciliationRoundCompleted(
                candidates.Count, queried, corrected, withoutLookup, retired);
        }

        return new DeliveryReconciliationResult(
            candidates.Count, queried, corrected, withoutLookup, unanswered, retired);
    }

    /// <summary>
    /// The attempts a provider was given and never reported back on, oldest
    /// first.
    /// <para>
    /// The two statuses are the whole eligibility, and they are also what keeps
    /// an attempt settled by its own validity out of here: that attempt never
    /// reached a provider, carries no message identity to ask by, and was
    /// written straight to a terminal status by the dispatcher that refused to
    /// send it. Asking about it would mean asking a provider about a message it
    /// was never given.
    /// </para>
    /// <para>
    /// The age falls back to the creation instant when the status stamp is
    /// empty, which is what every row written before that column existed
    /// carries. The scheduler cannot make that substitution, because acting
    /// early there costs a second message to a person; here the same mistake
    /// costs one provider read, and refusing to make it would leave exactly the
    /// rows this job was created to resolve permanently unreachable.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ReconciliationCandidate>> CandidatesAsync(
        DateTimeOffset threshold,
        int batchSize,
        CancellationToken cancellationToken)
        => await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => (attempt.Status == NotificationAttemptStatuses.Sent
                    || attempt.Status == NotificationAttemptStatuses.Unknown)
                && attempt.ProviderKey != null
                && (attempt.StatusChangedAt ?? attempt.CreatedAt) < threshold)
            .Join(
                db.Notifications.AsNoTracking(),
                attempt => attempt.NotificationId,
                notification => notification.Id,
                (attempt, notification) => new { attempt, notification })

            // Ordered before the projection, and by the columns themselves:
            // the oldest silence goes first, and an ordering written over the
            // projected record would leave the database with nothing to sort by.
            .OrderBy(pair => pair.attempt.StatusChangedAt ?? pair.attempt.CreatedAt)
            .Take(batchSize)
            .Select(pair => new ReconciliationCandidate(
                pair.attempt.Id,
                pair.attempt.NotificationId,
                pair.notification.RecipientId,
                pair.attempt.Channel,
                pair.attempt.Status,
                pair.attempt.ProviderKey!,
                pair.attempt.ProviderMessageId,
                pair.attempt.ContactPointId,
                pair.attempt.SentAt ?? pair.attempt.StatusChangedAt ?? pair.attempt.CreatedAt,
                pair.attempt.StatusChangedAt ?? pair.attempt.CreatedAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Asks one provider about one attempt and records whatever it answers.
    /// Returns how many answers actually moved the attempt, which is the only
    /// number this job exists to produce.
    /// </summary>
    private async Task<int> ReconcileAsync(
        IProviderDeliveryLookup lookup,
        ReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        DeliveryTarget? target = await ResolveTargetAsync(candidate, cancellationToken);
        Result<IReadOnlyList<ProviderDeliveryEvent>> answer = await lookup.LookupAsync(
            new ProviderDeliveryQuery(
                new DispatchCorrelation(candidate.NotificationId, candidate.AttemptId),
                candidate.ProviderMessageId,
                target,
                candidate.SentAt),
            cancellationToken);
        if (answer.IsFailure)
        {
            logger.ReconciliationUnanswered(
                candidate.AttemptId, candidate.ProviderKey, answer.Error ?? string.Empty);
            return 0;
        }

        var corrected = 0;
        foreach (ProviderDeliveryEvent found in answer.Value!)
            if (await ApplyAsync(found, candidate, cancellationToken)) corrected++;

        return corrected;
    }

    /// <summary>
    /// Records one discovered event as evidence and applies it, both exactly as
    /// the callback path does. A provider event this hub already honoured is
    /// refused by the shared identity ledger and applied by nobody a second
    /// time.
    /// </summary>
    private async Task<bool> ApplyAsync(
        ProviderDeliveryEvent found,
        ReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var sealedPayload = await evidenceWriter.SealPayloadAsync(
            JsonSerializer.SerializeToUtf8Bytes(found), cancellationToken);
        DeliveryEventRecorded recorded = await evidenceWriter.RecordDiscoveredAsync(
            found, found.Correlation, sealedPayload, cancellationToken);
        if (recorded.DeliveryEventId is not { } deliveryEventId)
        {
            logger.ReconciliationAlreadyHonoured(candidate.AttemptId, found.ProviderEventId);
            return false;
        }

        var kind = DeliveryEventKinds.From(found.Kind);
        DeliveryApplicationOutcome outcome = await applier.ApplyAsync(
            new DeliveryApplicationRequest
            {
                Event = found,
                DeliveryEventId = deliveryEventId,

                // No queue message drove this application, so there is no
                // message identity to mark: the identity that makes this
                // exactly-once is the provider's own, claimed above.
                DedupeMessageId = null,
            },
            cancellationToken);
        if (outcome is not DeliveryApplicationOutcome.Applied)
        {
            logger.ReconciliationAnswerIgnored(candidate.AttemptId, kind, candidate.Status);
            return false;
        }

        logger.ReconciliationCorrected(candidate.AttemptId, candidate.ProviderKey, kind);
        return true;
    }

    /// <summary>
    /// The destination of one attempt, revealed only when it is the only route
    /// left. A message the provider gave an identity to is asked about by that
    /// identity, and no destination is needed or resolved; a message with no
    /// identity can only be found by what it was addressed to, and the value is
    /// read here, handed to the adapter, and dropped with the query.
    /// <para>
    /// The rule is written in terms of the attempt and not of the provider, and
    /// that is a boundary decision rather than an approximation. Which search
    /// keys a provider platform offers is provider knowledge and lives on the
    /// other side of the published contract; what this module knows is whether
    /// the send produced an identity to ask by. The cost of the difference is
    /// one superfluous reveal for a provider that searches by metadata and
    /// whose send left no identity, and the value of that reveal is discarded
    /// unread. The cost of the alternative would be a table of provider
    /// capabilities maintained inside this module, drifting away from the
    /// adapters it describes.
    /// </para>
    /// <para>
    /// The value is never persisted, never logged and never returned. It exists
    /// between this method and the provider call, in memory, exactly as it does
    /// on the send path, and the canonical event that comes back carries no
    /// destination by contract.
    /// </para>
    /// </summary>
    private async Task<DeliveryTarget?> ResolveTargetAsync(
        ReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.ProviderMessageId is { Length: > 0 }) return null;

        if (candidate.ContactPointId is not { } contactPointId) return null;

        Result<string> revealed = await recipientDirectory.RevealContactValueAsync(
            candidate.RecipientId, contactPointId, cancellationToken);
        if (revealed.IsFailure)
        {
            // A contact point that no longer resolves leaves the query without
            // a destination. The adapter decides what that is worth: for a
            // provider that searches by metadata it is worth nothing, and for
            // one that does not it is a refusal this round records.
            logger.ReconciliationTargetUnavailable(
                candidate.AttemptId, revealed.Error ?? string.Empty);
            return null;
        }

        return string.Equals(candidate.Channel, Channel.Sms.Value, StringComparison.Ordinal)
            ? new SmsDeliveryTarget(revealed.Value!)
            : new EmailDeliveryTarget(revealed.Value!);
    }
}
