using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>The plan the relay's claim gets for one band of one arm over a synthetic backlog.</summary>
internal sealed record RelayPlan(
    string Arm,
    int Band,
    string BandName,
    int Backlog,
    double ExecutionMs,
    long RowsRemovedByFilter,
    bool ScansSequentially,
    bool SortsOnDisk,
    double BatchP50Ms,
    double BatchMaxMs,
    int BatchesDrained,
    IReadOnlyList<string> Plan);

/// <summary>
/// One schema shape the claim can be measured against: the statement the relay
/// would send, plus whatever the arm has to build before it and undo after it.
/// </summary>
internal sealed record RelayArm(
    string Id,
    string ClaimSql,
    IReadOnlyList<string> Setup,
    IReadOnlyList<string> Teardown);

/// <summary>
/// Reads the execution plan and the per-batch cost of the outbox claim over a
/// backlog large enough to separate an index scan from a walk. The question is
/// not latency on this host: it is which plan PostgreSQL picks, how many rows
/// it has to touch to fill one batch, and whether the shape the schema declares
/// is the shape that answers the claim. The arms exist so the answer is a
/// comparison instead of an assertion: the arm that drops the index is what
/// keeps the good plan from being a sentence that would pass on any schema.
/// </summary>
internal static class RelayPlanScenario
{
    /// <summary>The band expression as the schema stores it, spelled here because the probe is not a friend of the API assembly.</summary>
    private const string ClassificationSql =
        "CASE WHEN destination = 'core-auth' "
        + "OR (destination LIKE 'dispatch-%' AND destination LIKE '%-auth') THEN 0 "
        + "WHEN priority_class = 'critical' THEN 1 "
        + "WHEN priority_class = 'transactional' THEN 2 "
        + "ELSE 3 END";

    private const string StoredBandClaimSql = """
        SELECT id, destination, event_type, message_key, headers::text, payload::text, created_at
        FROM platform.outbox
        WHERE sent_at IS NULL
          AND transport = @transport
          AND priority_band = @band
        ORDER BY created_at
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string WrittenBandClaimSql = """
        SELECT id, destination, event_type, message_key, headers::text, payload::text, created_at
        FROM platform.outbox
        WHERE sent_at IS NULL
          AND transport = @transport
          AND probe_written_band = @band
        ORDER BY created_at
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string CreateSchemaIndexSql = """
        CREATE INDEX ix_outbox_pending ON platform.outbox (transport, priority_band, created_at)
        WHERE sent_at IS NULL
        """;

    private const string StampSql = "UPDATE platform.outbox SET sent_at = @sentAt WHERE id = ANY(@ids)";

    /// <summary>
    /// Batches drained per band while the clock runs. Ten is enough for a median
    /// on a shape whose per-batch cost is either sub-millisecond or hundreds of
    /// milliseconds, and it consumes a thousandth of the backlog, so the arm the
    /// probe measures next still sees the volume it was seeded with.
    /// </summary>
    private const int TimedBatches = 10;

    internal static async Task<IReadOnlyList<RelayPlan>> RunAsync(
        ProbeDatabase database,
        int backlog,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await BacklogSeeder.FillOutboxAsync(database, backlog, cancellationToken);

        var plans = new List<RelayPlan>();
        foreach (RelayArm arm in Arms())
        {
            foreach (var statement in arm.Setup)
            {
                await database.ExecuteAsync(statement, cancellationToken);
            }

            try
            {
                foreach ((var band, var name) in Bands())
                {
                    plans.Add(await MeasureAsync(
                        database, arm, backlog, band, name, batchSize, cancellationToken));
                }
            }
            finally
            {
                foreach (var statement in arm.Teardown)
                {
                    await database.ExecuteAsync(statement, cancellationToken);
                }
            }
        }

        return plans;
    }

    /// <summary>
    /// The arms, in the order they run. The schema as migrated comes first, on
    /// the freshest table. The written column comes last on purpose: filling it
    /// rewrites every row of the backlog, and a table carrying a million dead
    /// tuples would charge the next arm for this one's work.
    /// </summary>
    private static RelayArm[] Arms() =>
    [
        new(
            "índice do schema",
            StoredBandClaimSql,
            [],
            []),
        new(
            "índice derrubado",
            StoredBandClaimSql,
            ["DROP INDEX platform.ix_outbox_pending"],
            [CreateSchemaIndexSql]),
        new(
            "índice (banda, transporte)",
            StoredBandClaimSql,
            [
                "DROP INDEX platform.ix_outbox_pending",
                """
                CREATE INDEX ix_outbox_band_first ON platform.outbox (priority_band, transport, created_at)
                WHERE sent_at IS NULL
                """,
            ],
            ["DROP INDEX platform.ix_outbox_band_first", CreateSchemaIndexSql]),
        new(
            "coluna escrita",
            WrittenBandClaimSql,
            [
                "ALTER TABLE platform.outbox ADD COLUMN probe_written_band integer",
                $"UPDATE platform.outbox SET probe_written_band = {ClassificationSql}",
                """
                CREATE INDEX ix_outbox_written_band ON platform.outbox
                    (transport, probe_written_band, created_at)
                WHERE sent_at IS NULL
                """,
                "VACUUM ANALYZE platform.outbox",
            ],
            [
                "DROP INDEX platform.ix_outbox_written_band",
                "ALTER TABLE platform.outbox DROP COLUMN probe_written_band",
            ]),
    ];

    private static (int Band, string Name)[] Bands() =>
    [
        (0, "auth"),
        (1, "critical"),
        (3, "operational"),
    ];

    private static async Task<RelayPlan> MeasureAsync(
        ProbeDatabase database,
        RelayArm arm,
        int backlog,
        int band,
        string name,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Every band starts from the same place. Without this the band that
        // happens to be measured while the checkpointer is flushing the pages
        // the previous drain dirtied carries a tail that belongs to the host,
        // and the report would read it as the cost of the plan.
        await database.ExecuteAsync("CHECKPOINT", cancellationToken);
        IReadOnlyList<string> lines = await ExplainAsync(database, arm, band, batchSize, cancellationToken);
        await database.ExecuteAsync("CHECKPOINT", cancellationToken);
        (var p50, var max, var batches) = await DrainAsync(database, arm, band, batchSize, cancellationToken);
        return new RelayPlan(
            arm.Id,
            band,
            name,
            backlog,
            ExecutionMs(lines),
            Counter(lines, "Rows Removed by Filter:"),
            lines.Any(line => line.Contains("Seq Scan", StringComparison.Ordinal)),
            lines.Any(line => line.Contains("Disk:", StringComparison.Ordinal)),
            p50,
            max,
            batches,
            lines);
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        ProbeDatabase database,
        RelayArm arm,
        int band,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);

        // The claim locks the rows it reads, so the plan is taken inside a
        // transaction that is rolled back: measuring must not consume backlog.
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 0;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, VERBOSE false) " + arm.ClaimSql;
        command.Parameters.AddWithValue("transport", "sqs");
        command.Parameters.AddWithValue("band", band);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var lines = new List<string>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return lines;
    }

    /// <summary>
    /// Claims and stamps whole batches the way the relay does, committing each
    /// one. A rolled-back claim would re-read the same head of the backlog with
    /// everything already in cache, which is the one number nobody can act on:
    /// the budget of the design is spent on a batch that leaves the row behind.
    /// </summary>
    private static async Task<(double P50Ms, double MaxMs, int Batches)> DrainAsync(
        ProbeDatabase database,
        RelayArm arm,
        int band,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var samples = new LatencyHistogram();
        var batches = 0;
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);

        for (var batch = 0; batch < TimedBatches; batch++)
        {
            var started = Stopwatch.GetTimestamp();
            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            var claimed = new List<Guid>(batchSize);
            await using (NpgsqlCommand claim = connection.CreateCommand())
            {
                claim.Transaction = transaction;
                claim.CommandTimeout = 0;
                claim.CommandText = arm.ClaimSql;
                claim.Parameters.AddWithValue("transport", "sqs");
                claim.Parameters.AddWithValue("band", band);
                claim.Parameters.AddWithValue("batchSize", batchSize);
                await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add(reader.GetGuid(0));
                }
            }

            if (claimed.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                break;
            }

            await using (NpgsqlCommand stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandTimeout = 0;
                stamp.CommandText = StampSql;
                stamp.Parameters.AddWithValue("sentAt", DateTimeOffset.UtcNow);
                stamp.Parameters.AddWithValue("ids", claimed.ToArray());
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            batches++;
        }

        PhaseStatistics statistics = samples.Snapshot();
        return (statistics.P50, statistics.Max, batches);
    }

    private static double ExecutionMs(IReadOnlyList<string> lines)
    {
        var marker = lines.FirstOrDefault(line => line.StartsWith("Execution Time:", StringComparison.Ordinal));
        if (marker is null)
        {
            return double.NaN;
        }

        var value = marker.Replace("Execution Time:", string.Empty, StringComparison.Ordinal)
            .Replace("ms", string.Empty, StringComparison.Ordinal)
            .Trim();
        return double.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
    }

    /// <summary>Sums a counter the plan reports per node, so a plan with several nodes is not read from one of them.</summary>
    private static long Counter(IReadOnlyList<string> lines, string label)
    {
        long total = 0;
        foreach (var line in lines)
        {
            var at = line.IndexOf(label, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var digits = new string(
                [.. line[(at + label.Length)..].TrimStart().TakeWhile(char.IsDigit)]);
            if (long.TryParse(digits, CultureInfo.InvariantCulture, out var parsed))
            {
                total += parsed;
            }
        }

        return total;
    }
}
