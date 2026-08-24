using System.Globalization;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>
/// What the planner does with one statement the trail runs by sequence.
/// <c>SortsOnDisk</c> is the term the split of the range read exists to remove:
/// an ordering spilled to disk carried the canonical text of every row of the
/// partition through an external merge.
/// </summary>
internal sealed record ReadPathPlan(
    string Path,
    int Volume,
    double ExecutionMs,
    long Buffers,
    bool ScansSequentially,
    bool SortsOnDisk,
    IReadOnlyList<string> Plan);

/// <summary>
/// The three statements that walk a monthly partition by sequence: the tail
/// read the appender takes inside the advisory lock, the range read the
/// verification and the export share, and the high-water mark the export
/// planner asks for. The tail index is partial on the chain columns, and a
/// partial index only answers a statement that carries its predicate, so
/// whether the other two paths gain anything is a question for the planner
/// rather than for a claim in a document.
/// </summary>
internal static class ChainReadPathsScenario
{
    private const string TailSql = """
        SELECT hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
        ORDER BY seq DESC
        LIMIT 1
        """;

    private const string ChainedRangeSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND hash IS NOT NULL
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    private const string PreChainRangeSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND hash IS NULL
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    private const string MaxSeqSql = """
        SELECT GREATEST(
            COALESCE((
                SELECT MAX(seq) FROM audit.audit_event
                WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
                  AND hash IS NOT NULL), 0),
            COALESCE((
                SELECT MAX(seq) FROM audit.audit_event
                WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
                  AND hash IS NULL), 0))
        """;

    internal static async Task<IReadOnlyList<ReadPathPlan>> RunAsync(
        ProbeDatabase database,
        PartitionMonth month,
        int volume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);

        (string Path, string Sql)[] paths =
        [
            ("cauda da cadeia, dentro do lock do append", TailSql),
            ("faixa de seq, metade encadeada", ChainedRangeSql),
            ("faixa de seq, metade pré-cadeia", PreChainRangeSql),
            ("maior seq da partição, plano do export", MaxSeqSql),
        ];

        var plans = new List<ReadPathPlan>();
        foreach ((var path, var sql) in paths)
        {
            plans.Add(await ExplainAsync(database, month, path, sql, volume, cancellationToken));
        }

        return plans;
    }

    private static async Task<ReadPathPlan> ExplainAsync(
        ProbeDatabase database,
        PartitionMonth month,
        string path,
        string sql,
        int volume,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        var lines = new List<string>();

        // Twice, for the same reason the index comparison runs twice: a cold
        // plan and a warm one are not comparable, and the buffer counts of the
        // first pass belong to the cache rather than to the statement.
        for (var pass = 0; pass < 2; pass++)
        {
            lines.Clear();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + sql;
            command.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
            command.Parameters.AddWithValue("toExclusive", month.ToExclusive);
            if (sql == ChainedRangeSql || sql == PreChainRangeSql)
            {
                // One block, the size the reader walks with, not the whole
                // partition: measuring a statement nobody sends would answer a
                // question nobody asked.
                command.Parameters.AddWithValue("afterSeq", 0L);
                command.Parameters.AddWithValue("throughSeq", long.MaxValue);
                command.Parameters.AddWithValue("maxRows", 5_000);
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }
        }

        return new ReadPathPlan(
            path,
            volume,
            ExecutionMs(lines),
            Buffers(lines),
            lines.Exists(line => line.Contains("Seq Scan", StringComparison.Ordinal)),
            lines.Exists(line => line.Contains("Sort Method", StringComparison.Ordinal)
                && line.Contains("disk", StringComparison.Ordinal)),
            [.. lines]);
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

    /// <summary>
    /// Buffers touched by the whole statement. Counts accumulate up the tree,
    /// so the largest line is the root's, which is the number that describes
    /// the statement rather than one of its nodes.
    /// </summary>
    private static long Buffers(IEnumerable<string> lines)
    {
        long largest = 0;
        foreach (var line in lines)
        {
            var marker = line.IndexOf("Buffers:", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            long total = 0;
            foreach (var term in line[(marker + "Buffers:".Length)..]
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = term.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && long.TryParse(
                    term[(equals + 1)..], CultureInfo.InvariantCulture, out var count))
                {
                    total += count;
                }
            }

            largest = Math.Max(largest, total);
        }

        return largest;
    }
}
