using System.Globalization;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>The plan the relay's claim gets for one band over a synthetic backlog.</summary>
internal sealed record RelayPlan(
    int Band,
    string BandName,
    int Backlog,
    double ExecutionMs,
    IReadOnlyList<string> Plan);

/// <summary>
/// Reads the execution plan of the outbox claim over a backlog large enough to
/// separate an index scan from a walk. The question here is not latency on this
/// host: it is which plan PostgreSQL picks and how many rows it has to touch to
/// fill one batch, and a local instance answers both, because the band is a
/// CASE expression in the predicate that no index covers.
/// </summary>
internal static class RelayPlanScenario
{
    private const string ClaimSql = """
        SELECT id, destination, event_type, message_key, headers::text, payload::text, created_at
        FROM platform.outbox
        WHERE sent_at IS NULL
          AND transport = @transport
          AND CASE
                WHEN destination = 'core-auth'
                  OR (destination LIKE 'dispatch-%' AND destination LIKE '%-auth') THEN 0
                WHEN priority_class = 'critical' THEN 1
                WHEN priority_class = 'transactional' THEN 2
                ELSE 3
              END = @band
        ORDER BY created_at
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    internal static async Task<IReadOnlyList<RelayPlan>> RunAsync(
        ProbeDatabase database,
        int backlog,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await BacklogSeeder.FillOutboxAsync(database, backlog, cancellationToken);

        var plans = new List<RelayPlan>();
        foreach ((var band, var name) in Bands())
        {
            plans.Add(await ExplainAsync(database, backlog, band, name, batchSize, cancellationToken));
        }

        return plans;
    }

    private static (int Band, string Name)[] Bands() =>
    [
        (0, "auth"),
        (1, "critical"),
        (3, "operational"),
    ];

    private static async Task<RelayPlan> ExplainAsync(
        ProbeDatabase database,
        int backlog,
        int band,
        string name,
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
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, VERBOSE false) " + ClaimSql;
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
        return new RelayPlan(band, name, backlog, ExecutionMs(lines), lines);
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
