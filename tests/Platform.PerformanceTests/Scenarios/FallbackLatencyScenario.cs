using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NotificationHub.PerformanceTests.Infrastructure;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>
/// What one scheduler round costs on the fallback path, at one table volume.
/// </summary>
internal sealed record FallbackLatency(
    string Statement,
    int Notifications,
    int Claimed,
    bool ScansSequentially,
    PhaseStatistics Round,
    IReadOnlyList<string> Plan);

/// <summary>
/// Measures the only term of the fallback budget that grows with the data.
/// <para>
/// The promise is a fallback SMS within an accepted window of a degraded push,
/// and that window is a sum: the step deadline, one scheduler interval, two
/// queue hops, the Core stage and the provider call. Every term except one is
/// a fixed budget, already guarded by an arithmetic assertion in the unit
/// suite. The exception is this one: how long the round itself takes to find
/// the overdue attempts and ask for their next step. It is the term that grows
/// with retention, and nothing measured it.
/// </para>
/// <para>
/// What this measures is the claim as the scheduler sends it, over a table of
/// the requested size, with the overdue rows as a rare minority. What it does
/// not measure is the queue hops or the provider call, which leave this process
/// and belong to the load gate against a real environment. The report says so,
/// because a number labelled "fallback latency" that only covers the database
/// would be read as the whole budget.
/// </para>
/// <para>
/// One round is one statement and therefore one round trip, so the number is
/// comparable across volumes on the same bench. The report carries how many rows
/// each round claimed next to the time, because a batch that fills its limit at
/// the larger volume costs more for having claimed more, and a reader comparing
/// two volumes has to divide before concluding anything about the plan.
/// </para>
/// <para>
/// The statements are spelled here rather than imported, for the same reason
/// the relay scenario spells its own: the probe is deliberately not a friend of
/// the API assembly. They are transcriptions of the constants in
/// <c>OverdueFallbackScan</c>, and the plan assertions in the integration suite
/// are what keep those constants honest; this measures cost, not correctness.
/// </para>
/// </summary>
internal static class FallbackLatencyScenario
{
    /// <summary>Transcription of <c>OverdueFallbackScan.DeadlineClaimSql</c>.</summary>
    private const string DeadlineClaimSql = """
        SELECT attempt.id, attempt.created_at, attempt.fallback_deadline,
               notification.id, notification.recipient_id, notification.class, notification.auth_flow
        FROM notifications.notification_attempt AS attempt
        JOIN notifications.notification AS notification
          ON notification.id = attempt.notification_id
         AND notification.created_at > attempt.created_at - @attemptWindow
         AND notification.created_at <= attempt.created_at
        WHERE attempt.status IN ('queued', 'sent')
          AND attempt.fallback_deadline IS NOT NULL
          AND attempt.plan_advanced_at IS NULL
          AND attempt.fallback_requested_at IS NULL
          AND attempt.fallback_deadline < @now
          AND notification.status = 'dispatched'
        ORDER BY attempt.fallback_deadline
        LIMIT @batchSize
        FOR UPDATE OF attempt SKIP LOCKED
        """;

    /// <summary>Transcription of <c>OverdueFallbackScan.UnknownDeadlineClaimSql</c>.</summary>
    private const string UnknownDeadlineClaimSql = """
        SELECT attempt.id, attempt.created_at, attempt.fallback_deadline,
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
          AND attempt.fallback_deadline < @now
          AND notification.status = 'dispatched'
          AND (notification.class = 'critical' OR notification.auth_flow)
        ORDER BY attempt.fallback_deadline
        LIMIT @batchSize
        FOR UPDATE OF attempt SKIP LOCKED
        """;

    /// <summary>
    /// The sixty-day window the module bounds every attempt lookup by, spelled
    /// here for the same reason the statements are.
    /// </summary>
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromDays(60);

    internal static async Task<IReadOnlyList<FallbackLatency>> RunAsync(
        ProbeDatabase database,
        int notifications,
        int batchSize,
        int rounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        IReadOnlyList<string> populated = await PopulatedPartitionsAsync(database, cancellationToken);
        var measured = new List<FallbackLatency>
        {
            await MeasureAsync(
                database, "prazo vencido (queued e sent)", DeadlineClaimSql,
                notifications, batchSize, rounds, populated, cancellationToken),
            await MeasureAsync(
                database, "prazo vencido (unknown)", UnknownDeadlineClaimSql,
                notifications, batchSize, rounds, populated, cancellationToken),
        };
        return measured;
    }

    /// <summary>
    /// Partitions of the attempt table that actually hold rows.
    /// <para>
    /// A partitioned table always carries empty partitions for the months
    /// ahead, and the planner reads an empty one sequentially because there is
    /// nothing cheaper than reading nothing. Looking for the words "Seq Scan"
    /// anywhere in the plan therefore reports a walk against a perfectly
    /// indexed schema, which is how an instrument earns the reputation that
    /// gets it ignored. The question is whether a partition with rows in it is
    /// being walked.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> PopulatedPartitionsAsync(
        ProbeDatabase database,
        CancellationToken cancellationToken)
        => await database.TextsAsync(
            """
            SELECT child.relname
            FROM pg_inherits
            JOIN pg_class AS child ON child.oid = pg_inherits.inhrelid
            JOIN pg_class AS parent ON parent.oid = pg_inherits.inhparent
            JOIN pg_namespace AS schema ON schema.oid = parent.relnamespace
            WHERE parent.relname = 'notification_attempt'
              AND schema.nspname = 'notifications'
              AND child.reltuples > 0
            """,
            cancellationToken);

    /// <summary>
    /// Runs the claim as many times as asked and reports the distribution.
    /// <para>
    /// Every round runs inside a transaction that is rolled back. The claim
    /// locks the rows it selects, so a round that committed would leave the
    /// next round a different table and the series would measure the probe
    /// draining its own backlog instead of the cost of one round on a table of
    /// this size. Rolling back keeps every round asking the same question.
    /// </para>
    /// </summary>
    private static async Task<FallbackLatency> MeasureAsync(
        ProbeDatabase database,
        string label,
        string claimSql,
        int notifications,
        int batchSize,
        int rounds,
        IReadOnlyList<string> populatedPartitions,
        CancellationToken cancellationToken)
    {
        var samples = new LatencyHistogram();
        var claimed = 0;
        for (var round = 0; round < rounds; round++)
        {
            await using NpgsqlConnection connection =
                await database.DataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = claimSql;
            Bind(command, batchSize);

            var started = Stopwatch.GetTimestamp();
            var rows = 0;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken)) rows++;
            }

            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            claimed = rows;
            await transaction.RollbackAsync(cancellationToken);
        }

        IReadOnlyList<string> plan = await ExplainAsync(database, claimSql, batchSize, cancellationToken);
        var walks = plan.Any(line => line.Contains("Seq Scan", StringComparison.Ordinal)
            && populatedPartitions.Any(partition => line.Contains(partition, StringComparison.Ordinal)));
        return new FallbackLatency(
            label,
            notifications,
            claimed,
            walks,
            samples.Snapshot(),
            plan);
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        ProbeDatabase database,
        string claimSql,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();

        // The locking clause cannot appear inside EXPLAIN, and it changes no
        // access path: the rows are chosen first and locked afterwards.
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS) "
            + claimSql.Replace("FOR UPDATE OF attempt SKIP LOCKED", string.Empty, StringComparison.Ordinal);
        Bind(command, batchSize);

        var lines = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    private static void Bind(NpgsqlCommand command, int batchSize)
    {
        command.CommandTimeout = 0;
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz)
        {
            Value = DateTimeOffset.UtcNow,
        });
        command.Parameters.Add(new NpgsqlParameter("attemptWindow", NpgsqlDbType.Interval)
        {
            Value = AttemptWindow,
        });
        command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer)
        {
            Value = batchSize,
        });
    }
}
