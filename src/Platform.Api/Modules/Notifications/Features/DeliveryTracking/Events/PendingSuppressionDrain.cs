using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>One evidence row whose suppression signal never reached the ledger.</summary>
internal sealed record PendingSuppressionReport(
    Guid DeliveryEventId,
    string RecipientId,
    Guid ContactPointId,
    string Channel,
    string Signal,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Reports the suppression signals the applier could not hand over.
/// <para>
/// The report cannot join the transaction that applies a delivery event: the
/// ledger belongs to another context and reads its own history to decide, and
/// reporting before the transition commits would stop addressing a destination
/// on the strength of a callback that ended up applying nothing. So the applier
/// reports after the commit, where a transient failure of the contact module
/// used to lose the signal for good, because the event is already applied and
/// already deduplicated and no redelivery ever revisits it.
/// </para>
/// <para>
/// The retry lives in the evidence row instead of in the queue. An applied row
/// carrying a real signal and no report stamp is a report this hub still owes,
/// and that is a fact in the database rather than a message in flight, so it
/// survives the process that failed to deliver it.
/// </para>
/// <para>
/// Repeating a report is safe by construction: the ledger keys its idempotency
/// on the identity of the evidence row, so a retry that races the original
/// settles as already applied instead of counting a second refusal, which on a
/// channel that suppresses at the second one would take a reachable destination
/// away from a person who was refused once.
/// </para>
/// </summary>
internal sealed class PendingSuppressionDrain(
    NotificationsDbContext db,
    ISuppressionLedger suppressionLedger,
    IOptions<SchedulerScanOptions> options,
    TimeProvider timeProvider,
    ILogger<PendingSuppressionDrain> logger)
{
    /// <summary>Reports every owed signal this round claims and returns how many settled.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        SchedulerScanOptions settings = options.Value;
        IReadOnlyList<PendingSuppressionReport> pending = await PendingAsync(
            settings.BatchSize, cancellationToken);
        if (pending.Count == 0) return 0;

        var settled = 0;
        foreach (PendingSuppressionReport report in pending)
        {
            if (await ReportAsync(report, cancellationToken)) settled++;
        }

        logger.PendingSuppressionDrainCompleted(pending.Count, settled);
        return settled;
    }

    /// <summary>
    /// The evidence rows that still owe a report, oldest first.
    /// <para>
    /// The three conjuncts are spelled out literally because they are the
    /// predicate of the partial index behind them: this runs on the scheduler's
    /// interval and the set is empty almost always, so proving it empty has to
    /// cost an index probe and not a walk of every partition.
    /// </para>
    /// <para>
    /// The joins are what make the report possible at all: the ledger is
    /// addressed by recipient and contact point, and the evidence row carries
    /// neither. An attempt with no contact point is a push registration, which
    /// the applier already stamps as settled, so the inner join is the filter
    /// and not an omission.
    /// </para>
    /// <para>
    /// Both joins carry the creation window that lets the planner discard the
    /// partitions the row cannot be in, exactly as the rest of this module
    /// does. The identity of an attempt is a pair with its creation instant, so
    /// a lookup by identifier alone reaches every month the table holds.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<PendingSuppressionReport>> PendingAsync(
        int batchSize,
        CancellationToken cancellationToken)
        => await db.DeliveryEvents
            .AsNoTracking()
            .Where(evidence => evidence.SuppressionSignal != DeliverySuppressionSignals.None
                && evidence.AppliedAt != null
                && evidence.SuppressionReportedAt == null)
            .Join(
                db.NotificationAttempts.AsNoTracking(),
                evidence => evidence.AttemptId,
                attempt => attempt.Id,
                (evidence, attempt) => new { evidence, attempt })
            .Where(pair => pair.attempt.CreatedAt
                    > pair.evidence.ReceivedAt - NotificationPlanOutcome.AttemptWindow
                && pair.attempt.CreatedAt
                    <= pair.evidence.ReceivedAt + NotificationPlanOutcome.AttemptWindow)
            .Join(
                db.Notifications.AsNoTracking(),
                pair => pair.attempt.NotificationId,
                notification => notification.Id,
                (pair, notification) => new { pair.evidence, pair.attempt, notification })
            .Where(row => row.attempt.ContactPointId != null
                && row.notification.CreatedAt
                    > row.attempt.CreatedAt - NotificationPlanOutcome.AttemptWindow
                && row.notification.CreatedAt <= row.attempt.CreatedAt)
            .OrderBy(row => row.evidence.ReceivedAt)
            .Take(batchSize)
            .Select(row => new PendingSuppressionReport(
                row.evidence.Id,
                row.notification.RecipientId,
                row.attempt.ContactPointId!.Value,
                row.attempt.Channel,
                row.evidence.SuppressionSignal,
                row.evidence.ReceivedAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Hands one owed signal to the ledger and stamps the row when the ledger
    /// has an answer, whichever answer it is. A refusal the ledger states is
    /// settled: an unknown contact point and a channel that disagrees with it
    /// are decisions no retry changes. A fault leaves the row owed for the next
    /// round, which is the whole reason this scan exists.
    /// </summary>
    private async Task<bool> ReportAsync(
        PendingSuppressionReport pending,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        try
        {
            Result<SuppressionOutcome> reported = await suppressionLedger.ReportDeliveryFeedbackAsync(
                new SuppressionReport(
                    pending.RecipientId,
                    pending.ContactPointId,
                    pending.Channel,
                    pending.Signal,
                    pending.DeliveryEventId,

                    // The instant the hub observed the refusal, which for a
                    // report this late is the instant the callback arrived and
                    // never now: the ledger accumulates refusals inside a
                    // window, and dating a recovered report by the recovery
                    // would move a refusal into a window it does not belong to.
                    pending.ReceivedAt),
                cancellationToken);
            if (reported.IsFailure)
            {
                logger.PendingSuppressionRefused(
                    pending.DeliveryEventId, reported.Error ?? pending.Signal);
            }
            else
            {
                var settled = reported.Value.ToString();
                logger.PendingSuppressionReported(
                    pending.DeliveryEventId, pending.ContactPointId, settled);
            }

            await db.DeliveryEvents
                .Where(candidate => candidate.Id == pending.DeliveryEventId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.SuppressionReportedAt, now),
                    cancellationToken);
            return reported.IsSuccess;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.PendingSuppressionRetryFailed(pending.DeliveryEventId, exception);
            return false;
        }
    }
}
