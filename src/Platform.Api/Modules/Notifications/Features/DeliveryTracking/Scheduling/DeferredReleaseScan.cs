using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>What one round of the release scan handed back to the pipeline.</summary>
internal readonly record struct DeferredReleaseScanResult(int Released, TimeSpan? OldestOverdue);

/// <summary>
/// Hands a parked notification back to the Core once its release instant has
/// passed: the state returns to accepted, the queue message the pipeline reads
/// is written, and the trail records the release, all in one transaction.
/// <para>
/// The transition is the load-bearing half, not the message. The Core reads
/// any state other than accepted as a redelivery and answers it with a
/// duplicate trail and no effect, so a release that only enqueued would look
/// exactly like a working scheduler from every queue metric while the
/// notification stayed parked forever. Enqueuing without transitioning is the
/// difference between this slice working and appearing to work.
/// </para>
/// <para>
/// A row is claimed by locking it for the length of its own transaction rather
/// than by a conditional update over a batch, which is the opposite of what
/// the fallback scans do, for a reason that is not symmetry: this write ends
/// with an audit append, and the append holds the chain lock of the trail's
/// monthly partition until the transaction ends. One notification per
/// transaction keeps that hold as short as the work that earns it, instead of
/// making concurrent ingestion wait behind a whole batch.
/// </para>
/// <para>
/// Expiry is deliberately not re-decided here. A notification whose validity
/// ended while it waited is settled by the pipeline stages it is going back
/// to, and restating that rule in the scan would give the same question two
/// answers that can drift.
/// </para>
/// </summary>
internal sealed class DeferredReleaseScan(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    IOptions<SchedulerScanOptions> options,
    TimeProvider timeProvider,
    ILogger<DeferredReleaseScan> logger)
{
    /// <summary>
    /// Notifications whose release instant has passed, exactly as the
    /// statement reaches the database.
    /// <para>
    /// The deferred state is written literally because it is the predicate of
    /// the partial index this reads, and a partial index only answers a
    /// statement whose quals imply its predicate. Spelling the state as a bind
    /// value would leave the planner unable to prove the implication and turn
    /// the round into a walk of every partition of the table.
    /// </para>
    /// </summary>
    internal const string CandidateSql = """
        SELECT notification.id, notification.created_at
        FROM notifications.notification AS notification
        WHERE notification.status = 'deferred'
          AND notification.release_at <= @now
        ORDER BY notification.release_at
        LIMIT @batchSize
        """;

    /// <summary>
    /// Takes exclusive ownership of one candidate for the length of its
    /// transaction. Skipping a locked row is what makes two replicas safe:
    /// the loser does not wait for the winner and does not release twice, it
    /// simply finds nothing and moves to the next candidate.
    /// </summary>
    internal const string ClaimSql = """
        SELECT * FROM notifications.notification
        WHERE id = {0} AND created_at = {1} AND status = 'deferred'
        FOR UPDATE SKIP LOCKED
        """;

    public async Task<DeferredReleaseScanResult> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<(Guid Id, DateTimeOffset CreatedAt)> candidates =
            await CandidatesAsync(now, cancellationToken);

        var released = 0;
        DateTimeOffset? oldestReleaseAt = null;
        foreach ((Guid id, DateTimeOffset createdAt) in candidates)
        {
            DateTimeOffset? releaseAt = await TryReleaseAsync(id, createdAt, now, cancellationToken);
            if (releaseAt is null)
            {
                continue;
            }

            released++;
            if (oldestReleaseAt is null || releaseAt < oldestReleaseAt)
            {
                oldestReleaseAt = releaseAt;
            }
        }

        TimeSpan? oldest = oldestReleaseAt is { } instant ? now - instant : null;
        if (released > 0)
        {
            logger.DeferredNotificationsReleased(released, oldest ?? TimeSpan.Zero);
        }

        return new DeferredReleaseScanResult(released, oldest);
    }

    private async Task<IReadOnlyList<(Guid Id, DateTimeOffset CreatedAt)>> CandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            List<(Guid, DateTimeOffset)> candidates = [];
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = CandidateSql;
            ScanCommands.AddParameter(command, "now", now);
            ScanCommands.AddParameter(command, "batchSize", options.Value.BatchSize);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
            }

            return candidates;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Releases one candidate, or answers null when another replica owns it or
    /// it already left the deferred state. Returns the release instant of the
    /// notification it released, which is what the age signal is measured
    /// against.
    /// </summary>
    private async Task<DateTimeOffset?> TryReleaseAsync(
        Guid id,
        DateTimeOffset createdAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        // Materialized without composing anything onto the statement: a
        // locking clause has to reach the database in the statement that reads
        // the row, and letting the provider wrap this one in a subquery to add
        // a limit it does not need would put that guarantee at the mercy of a
        // translation detail. The predicate is the primary key, so the list is
        // one row or none.
        List<Notification> claimed = await db.Notifications
            .FromSqlRaw(ClaimSql, id, createdAt)
            .ToListAsync(cancellationToken);
        Notification? notification = claimed.FirstOrDefault();
        if (notification is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        DateTimeOffset? releaseAt = notification.ReleaseAt;
        notification.MarkReleased();
        await db.SaveChangesAsync(cancellationToken);

        // Before the audit append on purpose, like every other writer of this
        // module: the append takes the partition chain lock and holds it until
        // the transaction ends, so anything queued after it widens the window
        // concurrent ingestion waits on.
        var destination = DispatchMessages.CoreDestination(
            notification.Class, notification.AuthFlow);
        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
            DispatchMessages.BuildNotificationAccepted(
                notification.RecipientId,
                notification.Class,
                notification.AuthFlow,
                notification.Id,
                now,
                Activity.Current?.Id),
            cancellationToken);
        await auditTrail.AppendAsync(
            transaction.GetDbTransaction(),
            new AuditEntry
            {
                ActorType = SchedulerAuditVocabulary.ActorTypeSystem,
                ActorId = SchedulerAuditVocabulary.ActorIdDeliveryTracker,
                Application = notification.Application,
                Action = SchedulerAuditVocabulary.NotificationReleased,
                EntityType = SchedulerAuditVocabulary.EntityTypeNotification,
                EntityId = notification.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    @class = notification.Class,
                    releaseAt,
                    destination,
                }),
                OccurredAt = now,
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return releaseAt;
    }
}
