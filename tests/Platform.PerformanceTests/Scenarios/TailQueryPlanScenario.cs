using System.Globalization;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>One index shape and what the tail query does with it.</summary>
internal sealed record TailPlan(
    string Variant,
    string? IndexDefinition,
    int Volume,
    double ExecutionMs,
    IReadOnlyList<string> Plan);

/// <summary>Which index shape the arm with the mitigations should carry, and why.</summary>
internal sealed record TailIndexChoice(string Variant, string? CreateSql, IReadOnlyList<TailPlan> Plans);

/// <summary>
/// The query that runs inside the lock on every append, read against the index
/// shapes that could answer it. Choosing an index by intuition and then
/// measuring the arm that carries it would prove nothing: a composite index
/// whose leading column is the partition key still has to walk the partition
/// to find the highest sequence, and only the plan says whether it does.
/// </summary>
internal static class TailQueryPlanScenario
{
    private const string TailSql = """
        SELECT hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
        ORDER BY seq DESC
        LIMIT 1
        """;

    /// <summary>
    /// The index shape the architecture ratified after the plan comparison:
    /// partial on the sequence, descending. A composite led by the partition
    /// key does not answer this read, because pruning already satisfied the
    /// time predicate and what remains is a range rather than an equality, so
    /// the composite provides no ordering by sequence inside it. The partial
    /// predicate has to appear literally in the query for the planner to match
    /// it, and it does.
    /// </summary>
    internal static string RatifiedIndexSql(PartitionMonth month)
    {
        ArgumentNullException.ThrowIfNull(month);
        return $"""
            CREATE INDEX ix_probe_chain_tail_seq ON audit."{month.Name}" (seq DESC)
            WHERE hash IS NOT NULL
            """;
    }

    internal static async Task<TailIndexChoice> RunAsync(
        ProbeDatabase database,
        PartitionMonth month,
        int volume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);

        (string Variant, string? Create)[] variants =
        [
            ("sem índice", null),
            (
                "occurred_at, seq DESC parcial",
                $"""
                 CREATE INDEX ix_probe_chain_tail_composite ON audit."{month.Name}" (occurred_at, seq DESC)
                 WHERE hash IS NOT NULL
                 """),
            (
                "seq DESC parcial",
                $"""
                 CREATE INDEX ix_probe_chain_tail_seq ON audit."{month.Name}" (seq DESC)
                 WHERE hash IS NOT NULL
                 """),
        ];

        var plans = new List<TailPlan>();
        foreach ((var variant, var create) in variants)
        {
            if (create is not null)
            {
                await database.ExecuteAsync(create, cancellationToken);
                await database.ExecuteAsync($"""ANALYZE audit."{month.Name}" """, cancellationToken);
            }

            plans.Add(await ExplainAsync(database, month, variant, create, volume, cancellationToken));
            if (create is not null)
            {
                await database.ExecuteAsync(DropOf(create), cancellationToken);
            }
        }

        TailPlan best = plans.MinBy(plan => plan.ExecutionMs)!;
        (var _, var chosen) = Array.Find(variants, entry => entry.Variant == best.Variant);
        return new TailIndexChoice(best.Variant, chosen, plans);
    }

    /// <summary>
    /// Whether the schema already carries an index that answers the tail read.
    /// </summary>
    /// <remarks>
    /// Behavioral on purpose: it reads the plan instead of matching an index
    /// name, so the probe stops creating an index of its own the moment a
    /// migration provides one that works. Until then the arm carries its own
    /// index and the volume-dependence guard watches the probe's index rather
    /// than production's, which is worth knowing when reading the gate.
    /// </remarks>
    internal static async Task<bool> SchemaAnswersTailAsync(
        ProbeDatabase database,
        PartitionMonth month,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "EXPLAIN " + TailSql;
        command.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
        command.Parameters.AddWithValue("toExclusive", month.ToExclusive);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0).Contains("Seq Scan", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static string DropOf(string createSql)
    {
        ArgumentNullException.ThrowIfNull(createSql);
        var name = createSql.Split("CREATE INDEX ", StringSplitOptions.None)[1].Split(' ')[0];
        return string.Create(CultureInfo.InvariantCulture, $"DROP INDEX IF EXISTS audit.{name}");
    }

    private static async Task<TailPlan> ExplainAsync(
        ProbeDatabase database,
        PartitionMonth month,
        string variant,
        string? definition,
        int volume,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        var lines = new List<string>();

        // Twice: the first pass warms the cache, and comparing a cold plan to a
        // warm one would rank the index shapes by luck of the buffer pool.
        for (var pass = 0; pass < 2; pass++)
        {
            lines.Clear();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + TailSql;
            command.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
            command.Parameters.AddWithValue("toExclusive", month.ToExclusive);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }
        }

        return new TailPlan(variant, definition, volume, ExecutionMs(lines), [.. lines]);
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
}
