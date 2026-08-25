using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>What one round of the two overdue scans claimed and asked for.</summary>
internal readonly record struct OverdueFallbackScanResult(
    int DeadlineRequested,
    int UnknownRequested,
    int StaleRequestsReleased,
    TimeSpan? OldestOverdue);

/// <summary>One attempt the scan claimed, with everything a trigger needs.</summary>
internal readonly record struct OverdueAttempt(
    Guid AttemptId,
    DateTimeOffset AttemptCreatedAt,
    DateTimeOffset OverdueSince,
    Guid NotificationId,
    string RecipientId,
    string PriorityClass,
    bool AuthFlow);

/// <summary>
/// The two scans that ask the Core for the next plan step when no provider
/// answer arrived: a fallback deadline that elapsed, and a send parked on an
/// inconclusive verdict for longer than the grace period.
/// <para>
/// The scan never claims the plan advance. That claim belongs to the handler,
/// which is where every trigger of a step meets every other one; a scan that
/// stamped it would make the handler read the step as already advanced and
/// drop the very trigger the scan just wrote. What the scan claims is the
/// right to ask, and it claims it twice over: the row lock of
/// <c>FOR UPDATE SKIP LOCKED</c> keeps two replicas of this role out of each
/// other's batch inside a round, and the request stamp keeps this round from
/// asking again on the next one while the message it wrote is still in flight.
/// </para>
/// <para>
/// Row locking rather than an optimistic conditional update, for these two:
/// the candidate rows have to be read and their notifications joined before
/// anything can be written, and a batch of asks carries no audit append, so
/// holding the batch in one transaction costs nothing anyone else waits on.
/// The release scan makes the opposite choice for the opposite reason.
/// </para>
/// </summary>
internal sealed class OverdueFallbackScan(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    IOptions<SchedulerScanOptions> options,
    TimeProvider timeProvider,
    ILogger<OverdueFallbackScan> logger)
{
    /// <summary>
    /// Attempts whose fallback deadline passed with no answer, exactly as the
    /// statement reaches the database, so the plan assertion reads this and
    /// never a transcription of it.
    /// <para>
    /// The three conjuncts of the partial index appear literally and together:
    /// a partial index only answers a statement whose quals imply its
    /// predicate, and a predicate spelled as a bind value cannot imply
    /// anything the planner is able to prove. Rewriting any of them as a
    /// parameter silently turns this into a sequential walk of every
    /// partition.
    /// </para>
    /// <para>
    /// The join is what keeps a concluded notification out of the batch. An
    /// attempt whose plan ended without advancing the step keeps a deadline
    /// and an empty claim forever, so without the state of its notification in
    /// the predicate the scan would ask for its next step once per round for
    /// the life of the partition, and the handler would answer every one of
    /// them with a duplicate trail. The window over the creation instant is
    /// what lets the planner discard the partitions the notification cannot
    /// possibly be in.
    /// </para>
    /// </summary>
    internal const string DeadlineClaimSql = """
        SELECT attempt.id, attempt.created_at, attempt.fallback_deadline,
               notification.id, notification.recipient_id, notification.class, notification.auth_flow
        FROM notifications.notification_attempt AS attempt
        JOIN notifications.notification AS notification
          ON notification.id = attempt.notification_id
         AND notification.created_at > attempt.created_at - @attemptWindow
         AND notification.created_at <= attempt.created_at
        WHERE attempt.status = 'sent'
          AND attempt.fallback_deadline IS NOT NULL
          AND attempt.plan_advanced_at IS NULL
          AND attempt.fallback_requested_at IS NULL
          AND attempt.fallback_deadline < @now
          AND notification.status = 'dispatched'
        ORDER BY attempt.fallback_deadline
        LIMIT @batchSize
        FOR UPDATE OF attempt SKIP LOCKED
        """;

    /// <summary>
    /// Attempts parked on an inconclusive verdict past the grace period, in
    /// the two flows where waiting costs more than asking again.
    /// <para>
    /// The eligibility of the class is a predicate and not a filter in code,
    /// so an ineligible attempt never occupies a slot of the batch. Both
    /// halves are read from what the notification already stores: the class,
    /// and the authentication signal the acceptance materialized precisely so
    /// that no producer of a trigger has to reach the published catalog.
    /// </para>
    /// <para>
    /// A null age never matches, which is what every row written before the
    /// column existed carries. That is the intended reading rather than an
    /// oversight: a scan must not act on an age nobody can compute.
    /// </para>
    /// <para>
    /// A stamped deadline is required here too, and it is not redundant with
    /// the plan claim. It is the proof that a later step exists at all, so an
    /// unresolved last step is left to reconciliation instead of being turned
    /// into a failed notification by a handler that would find no step to take.
    /// </para>
    /// </summary>
    internal const string UnknownClaimSql = """
        SELECT attempt.id, attempt.created_at, attempt.status_changed_at,
               notification.id, notification.recipient_id, notification.class, notification.auth_flow
        FROM notifications.notification_attempt AS attempt
        JOIN notifications.notification AS notification
          ON notification.id = attempt.notification_id
         AND notification.created_at > attempt.created_at - @attemptWindow
         AND notification.created_at <= attempt.created_at
        WHERE attempt.status = 'unknown'
          AND attempt.fallback_deadline IS NOT NULL
          AND attempt.plan_advanced_at IS NULL
          AND attempt.fallback_requested_at IS NULL
          AND attempt.status_changed_at < @threshold
          AND notification.status = 'dispatched'
          AND (notification.class = 'critical' OR notification.auth_flow)
        ORDER BY attempt.status_changed_at
        LIMIT @batchSize
        FOR UPDATE OF attempt SKIP LOCKED
        """;

    /// <summary>
    /// Records that this round asked, in the same transaction as the ask. The
    /// creation bounds prune the partitions to the ones the batch actually
    /// touched, and the null guard makes the write agree with the row lock
    /// that already made this batch exclusive.
    /// </summary>
    internal const string StampRequestSql = """
        UPDATE notifications.notification_attempt
           SET fallback_requested_at = @now
         WHERE id = ANY(@ids)
           AND created_at >= @createdFrom
           AND created_at <= @createdTo
           AND fallback_requested_at IS NULL
        """;

    /// <summary>
    /// Ages out the requests nobody answered, putting their attempts back in
    /// front of the scan. One statement instead of a second scan on purpose:
    /// clearing the stamp restores the row to the index the ordinary scan
    /// already reads, so the eligibility rules stay written once.
    /// <para>
    /// The predicate is the partial index over the requests in flight, spelled
    /// literally for the same reason the two scans spell theirs.
    /// </para>
    /// </summary>
    internal const string ReleaseStaleRequestSql = """
        UPDATE notifications.notification_attempt
           SET fallback_requested_at = NULL
         WHERE fallback_deadline IS NOT NULL
           AND plan_advanced_at IS NULL
           AND fallback_requested_at IS NOT NULL
           AND fallback_requested_at < @staleBefore
        """;

    public async Task<OverdueFallbackScanResult> RunAsync(CancellationToken cancellationToken)
    {
        SchedulerScanOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();

        var released = await ReleaseStaleRequestsAsync(
            now - settings.FallbackRequestRetry, cancellationToken);

        IReadOnlyList<OverdueAttempt> byDeadline = await RequestAsync(
            DeadlineClaimSql,
            command => ScanCommands.AddParameter(command, "now", now),
            now,
            settings,
            cancellationToken);
        IReadOnlyList<OverdueAttempt> byAge = await RequestAsync(
            UnknownClaimSql,
            command => ScanCommands.AddParameter(command, "threshold", now - settings.UnknownGrace),
            now,
            settings,
            cancellationToken);

        TimeSpan? oldest = Oldest(now, byDeadline, byAge);
        if (byDeadline.Count > 0 || byAge.Count > 0 || released > 0)
        {
            logger.OverdueFallbackRequested(
                byDeadline.Count, byAge.Count, released, oldest ?? TimeSpan.Zero);
        }

        return new OverdueFallbackScanResult(byDeadline.Count, byAge.Count, released, oldest);
    }

    /// <summary>
    /// Claims one batch and writes one trigger per claimed attempt, with the
    /// request stamps in the same transaction. Nothing is asked outside that
    /// transaction: a trigger on the queue whose stamp did not commit would be
    /// asked again on the very next round, and a stamp whose trigger did not
    /// commit would silence the attempt until the request aged out.
    /// </summary>
    private async Task<IReadOnlyList<OverdueAttempt>> RequestAsync(
        string claimSql,
        Action<DbCommand> bindScanParameter,
        DateTimeOffset now,
        SchedulerScanOptions settings,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        DbConnection connection = db.Database.GetDbConnection();
        List<OverdueAttempt> claimed = [];
        await using (DbCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = claimSql;
            bindScanParameter(command);
            ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
            ScanCommands.AddParameter(command, "batchSize", settings.BatchSize);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new OverdueAttempt(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetBoolean(6)));
            }
        }

        if (claimed.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return claimed;
        }

        foreach (OverdueAttempt attempt in claimed)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(),
                DispatchMessages.BuildFallbackRequested(
                    attempt.RecipientId,
                    attempt.PriorityClass,
                    attempt.AuthFlow,
                    attempt.NotificationId,
                    attempt.AttemptId,
                    now,
                    Activity.Current?.Id),
                cancellationToken);
        }

        await StampRequestsAsync(transaction, connection, claimed, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    private static async Task StampRequestsAsync(
        IDbContextTransaction transaction,
        DbConnection connection,
        IReadOnlyList<OverdueAttempt> claimed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = StampRequestSql;
        ScanCommands.AddParameter(command, "now", now);
        ScanCommands.AddParameter(command, "ids", claimed.Select(attempt => attempt.AttemptId).ToArray());
        ScanCommands.AddParameter(command, "createdFrom", claimed.Min(attempt => attempt.AttemptCreatedAt));
        ScanCommands.AddParameter(command, "createdTo", claimed.Max(attempt => attempt.AttemptCreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ReleaseStaleRequestsAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = ReleaseStaleRequestSql;
            ScanCommands.AddParameter(command, "staleBefore", staleBefore);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Age of the oldest row this round found overdue, which is the signal a
    /// stalled scheduler shows first: the batches stay full and this number
    /// grows while nothing ever fails.
    /// </summary>
    private static TimeSpan? Oldest(
        DateTimeOffset now,
        IReadOnlyList<OverdueAttempt> byDeadline,
        IReadOnlyList<OverdueAttempt> byAge)
    {
        DateTimeOffset? oldest = byDeadline.Concat(byAge)
            .Select(attempt => (DateTimeOffset?)attempt.OverdueSince)
            .Min();
        return oldest is { } since ? now - since : null;
    }
}
