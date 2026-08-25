using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>
/// Retires the attempts of concluded notifications from the scheduler's
/// indexes.
/// <para>
/// The debt is structural rather than accidental. A notification that ends
/// without a next step ends through the terminal settlement of the fallback
/// handler, which never claims the plan advance, because there is no advance
/// to claim: the plan is over. The attempt is left with a fallback deadline
/// stamped and an empty claim, and that pair is precisely the predicate of the
/// three partial indexes the scheduler reads. The scan itself is safe, because
/// its join keeps a concluded notification out of the batch, so nothing is
/// asked twice and nobody is messaged twice. What is not safe is the arithmetic
/// underneath it: the rows never leave the indexes, so every round pays to read
/// and discard a set that only grows, for the whole life of the partition.
/// </para>
/// <para>
/// The cure is to write the claim the terminal settlement never had a reason to
/// write, from the one job whose business is exactly the rows nobody came back
/// for. Stamping it changes no behaviour: the handler answers a trigger for a
/// concluded notification as a duplicate before it ever reaches the claim, and
/// a plan that ended cannot advance again. It changes only which rows the
/// indexes hold.
/// </para>
/// </summary>
internal sealed class ScanIndexLiabilitySweep(
    NotificationsDbContext db,
    IOptions<DeliveryReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<ScanIndexLiabilitySweep> logger)
{
    /// <summary>
    /// The retirement, exactly as it reaches the database, so a plan assertion
    /// reads this and never a transcription of it.
    /// <para>
    /// The three conjuncts of the deadline index appear literally and together
    /// in the subquery, for the same reason the scheduler's own statements
    /// spell theirs: a partial index only answers a statement whose quals imply
    /// its predicate, and this statement has no equality to seek by, so the
    /// implication is the whole of what makes the planner read the small index
    /// instead of walking every partition of the table.
    /// </para>
    /// <para>
    /// The window over the creation instants is what lets the planner discard
    /// the partitions a notification cannot have attempts in, and the second
    /// half of the key in the final match is what keeps the update inside the
    /// partition the subquery found the row in.
    /// </para>
    /// </summary>
    internal const string RetireSql = """
        WITH concluded AS (
            SELECT attempt.id, attempt.created_at
            FROM notifications.notification_attempt AS attempt
            JOIN notifications.notification AS notification
              ON notification.id = attempt.notification_id
             AND notification.created_at > attempt.created_at - @attemptWindow
             AND notification.created_at <= attempt.created_at
            WHERE attempt.fallback_deadline IS NOT NULL
              AND attempt.plan_advanced_at IS NULL
              AND attempt.fallback_requested_at IS NULL
              AND notification.status <> 'dispatched'
            LIMIT @batchSize
        )
        UPDATE notifications.notification_attempt AS attempt
           SET plan_advanced_at = @now
          FROM concluded
         WHERE attempt.id = concluded.id
           AND attempt.created_at = concluded.created_at
        """;

    /// <summary>Runs one bounded retirement and returns how many rows left the indexes.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        DeliveryReconciliationOptions settings = options.Value;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = RetireSql;
            ScanCommands.AddParameter(command, "now", timeProvider.GetUtcNow());
            ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
            ScanCommands.AddParameter(command, "batchSize", settings.LiabilityBatchSize);
            var retired = await command.ExecuteNonQueryAsync(cancellationToken);
            if (retired > 0) logger.ScanIndexLiabilityRetired(retired);

            return retired;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
