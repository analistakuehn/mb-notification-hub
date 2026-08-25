using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Suppression;

/// <summary>
/// Write side of the published suppression ledger. One reported refusal
/// commits its signal row, the suppression it may have completed, the
/// cache-invalidation message, the outgoing announcement and the audit event
/// in a single transaction, through the same transactional writer every other
/// write of this module uses.
/// <para>
/// Idempotency is the unique key over the source event, not a check before the
/// insert: two redeliveries racing each other would both read absent and both
/// count. The check that runs first is the fast path; the constraint is the
/// guard, and a report that loses the race settles as the declarative no-op it
/// is, with a trail of its own and no second effect.
/// </para>
/// <para>
/// Whether a refusal costs the recipient a channel is decided here, over the
/// history of refusals of the contact point, because that history is contact
/// data. Handing it to the reporter so the reporter could decide would export
/// exactly what this module exists to hold.
/// </para>
/// </summary>
internal sealed class SuppressionLedger(
    ContactConsentDbContext db,
    ContactConsentWriter writer,
    TimeProvider timeProvider,
    ILogger<SuppressionLedger> logger) : ISuppressionLedger
{
    public async Task<Result<SuppressionOutcome>> ReportDeliveryFeedbackAsync(
        SuppressionReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.RecipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.Reason);

        ContactPoint? point = await db.ContactPoints
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == report.ContactPointId
                    && candidate.RecipientId == report.RecipientId,
                cancellationToken);
        if (point is null)
        {
            return Result.NotFound<SuppressionOutcome>(
                "O ponto de contato não existe ou pertence a outro destinatário.");
        }

        if (!string.Equals(point.Channel, report.Channel, StringComparison.Ordinal))
        {
            // A rule of one channel must never settle another: e-mail suppresses
            // on the first refusal and the remaining channels do not.
            return Result.BusinessRuleViolation<SuppressionOutcome>(
                $"O ponto de contato pertence ao canal '{point.Channel}' e o relato declarou "
                + $"'{report.Channel}'.");
        }

        var writeContext = new ContactWriteContext(
            ContactConsentAuditVocabulary.ActorIdDeliveryFeedback,
            ContactConsentAuditVocabulary.ActorTypeSystem,
            Provenance: null);

        if (await AlreadyReportedAsync(report.SourceEventId, cancellationToken))
        {
            return await SettleRepeatedReportAsync(report, point, writeContext, cancellationToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        db.SuppressionSignals.Add(SuppressionSignalRecord.Report(
            point.Id, point.Channel, report.Reason, report.SourceEventId, report.ObservedAt));

        List<DateTimeOffset> observations = await db.SuppressionSignals
            .AsNoTracking()
            .Where(signal => signal.ContactPointId == point.Id)
            .Select(signal => signal.ObservedAt)
            .ToListAsync(cancellationToken);

        // The row just added is not in the store yet, so the decision counts it
        // here: the rule reads the refusals as they will stand after the commit.
        observations.Add(report.ObservedAt);

        var alreadySuppressed = await db.Suppressions
            .AsNoTracking()
            .AnyAsync(
                suppression => suppression.ContactPointId == point.Id && suppression.RemovedAt == null,
                cancellationToken);
        var suppresses = !alreadySuppressed
            && SuppressionRules.IsMet(point.Channel, observations, observations.Max());

        List<OutboxAppend> messages = [];
        if (suppresses)
        {
            db.Suppressions.Add(ContactSuppression.Create(
                point.Id,
                point.Channel,
                report.Reason,
                SuppressionSources.ProviderFeedback,
                ContactConsentAuditVocabulary.ActorTypeSystem,
                ContactConsentAuditVocabulary.ActorIdDeliveryFeedback,
                now));

            // The invalidation first, the announcement second: the hub's own
            // cache has to stop answering with the old snapshot before anyone
            // outside learns the channel is gone.
            messages.Add(ContactConsentEvents.Build(
                ContactConsentEvents.ContactChanged, report.RecipientId, point.Id, now));
            messages.Add(ContactConsentEvents.BuildContactSuppressed(new ContactSuppressedFact
            {
                RecipientId = report.RecipientId,
                Channel = point.Channel,
                Reason = report.Reason,
                OccurredAt = now,
            }));
        }

        AuditEntry auditEntry = BuildAuditEntry(
            report,
            point,
            suppresses ? ContactConsentAuditVocabulary.SuppressionAdded : ContactConsentAuditVocabulary.SuppressionSignalRecorded,
            new SuppressionAuditDetails(
                Suppressed: suppresses,
                AlreadySuppressed: alreadySuppressed,
                AlreadyApplied: false,
                Occurrences: observations.Count),
            now);

        ContactWriteOutcome persisted = await writer.CommitAsync(
            writeContext, messages, auditEntry, cancellationToken);
        if (persisted is ContactWriteOutcome.Duplicate)
        {
            throw new InvalidOperationException(
                "O relato de supressão não carrega marca de deduplicação; "
                + "um desfecho duplicado é impossível nesse caminho.");
        }

        if (persisted is ContactWriteOutcome.ConcurrencyConflict)
        {
            return await SettleConflictAsync(report, point, writeContext, cancellationToken);
        }

        if (!suppresses)
        {
            logger.SuppressionSignalRecorded(report.RecipientId, point.Id, report.Reason, observations.Count);
            return Result.Success(SuppressionOutcome.SignalRecorded);
        }

        logger.ContactSuppressed(report.RecipientId, point.Id, point.Channel, report.Reason);
        return Result.Success(SuppressionOutcome.ContactSuppressed);
    }

    private Task<bool> AlreadyReportedAsync(Guid sourceEventId, CancellationToken cancellationToken)
        => db.SuppressionSignals
            .AsNoTracking()
            .AnyAsync(signal => signal.SourceEventId == sourceEventId, cancellationToken);

    /// <summary>
    /// The declarative no-op of this path: no state change, no cache event, no
    /// announcement, and its own short trail, so a redelivery of the internal
    /// message is visible as what it was instead of leaving no record at all.
    /// </summary>
    private async Task<Result<SuppressionOutcome>> SettleRepeatedReportAsync(
        SuppressionReport report,
        ContactPoint point,
        ContactWriteContext writeContext,
        CancellationToken cancellationToken)
    {
        await writer.AppendStandaloneAuditAsync(
            writeContext,
            BuildAuditEntry(
                report,
                point,
                ContactConsentAuditVocabulary.SuppressionSignalRecorded,
                new SuppressionAuditDetails(
                    Suppressed: false, AlreadySuppressed: false, AlreadyApplied: true, Occurrences: 0),
                timeProvider.GetUtcNow()),
            cancellationToken);
        logger.SuppressionReportRepeated(report.RecipientId, point.Id, report.SourceEventId);
        return Result.Success(SuppressionOutcome.AlreadyApplied);
    }

    /// <summary>
    /// A unique key refused the write. Either the same source event landed
    /// concurrently, which is the no-op above arriving by the other door, or
    /// two reports raced for the single suppression in force, which the caller
    /// retries.
    /// </summary>
    private async Task<Result<SuppressionOutcome>> SettleConflictAsync(
        SuppressionReport report,
        ContactPoint point,
        ContactWriteContext writeContext,
        CancellationToken cancellationToken)
    {
        if (await AlreadyReportedAsync(report.SourceEventId, cancellationToken))
        {
            return await SettleRepeatedReportAsync(report, point, writeContext, cancellationToken);
        }

        logger.SuppressionWriteConflict(report.RecipientId, point.Id);
        return Result.BusinessRuleViolation<SuppressionOutcome>(
            "Uma escrita concorrente venceu a corrida; repita o relato de supressão.");
    }

    private static AuditEntry BuildAuditEntry(
        SuppressionReport report,
        ContactPoint point,
        string action,
        SuppressionAuditDetails details,
        DateTimeOffset now)
        => new()
        {
            ActorType = ContactConsentAuditVocabulary.ActorTypeSystem,
            ActorId = ContactConsentAuditVocabulary.ActorIdDeliveryFeedback,
            Application = null,
            Action = action,
            EntityType = ContactConsentAuditVocabulary.EntityTypeContactPoint,
            EntityId = point.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                recipientId = report.RecipientId,
                channel = point.Channel,
                reason = report.Reason,
                source = SuppressionSources.ProviderFeedback,
                sourceEventId = report.SourceEventId,
                observedAt = report.ObservedAt,
                occurrences = details.Occurrences,
                suppressed = details.Suppressed,
                alreadySuppressed = details.AlreadySuppressed,
                alreadyApplied = details.AlreadyApplied,
            }),
            OccurredAt = now,
        };

    /// <summary>What the trail says about one reported refusal beyond its identity.</summary>
    private sealed record SuppressionAuditDetails(
        bool Suppressed,
        bool AlreadySuppressed,
        bool AlreadyApplied,
        int Occurrences);
}
