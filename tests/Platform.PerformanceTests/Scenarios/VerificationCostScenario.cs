using System.Diagnostics;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>What one full replay of the open partition costs at a given size.</summary>
internal sealed record VerificationCost(
    int Volume,
    int RowsRead,
    int ChainedRows,
    double Seconds,
    int Breaks,
    int Forks,
    int Relinks,
    long FirstBrokenSeq,
    string FirstDiagnosis)
{
    internal bool Intact => Breaks == 0;

    internal double SecondsPer100K => RowsRead > 0 ? Seconds * 100_000 / RowsRead : double.NaN;
}

/// <summary>
/// Replays the whole open partition the way the periodic verification does:
/// batches in sequence order, hash of the previous link folded into the
/// canonical text of the next. No target was ever fixed for this cost, so the
/// scenario reports the curve instead of a verdict, and the cadence decision
/// belongs to whoever knows the production volume.
/// </summary>
internal static class VerificationCostScenario
{
    private const int BatchSize = 20_000;

    private const string SelectRowsSql = """
        SELECT seq, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND seq > @afterSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    internal static async Task<VerificationCost> RunAsync(
        ProbeDatabase database,
        PartitionMonth month,
        int volume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);

        var running = AuditChainArithmetic.PartitionAnchor(month.Name);
        var cursor = 0L;
        var rows = 0;
        var chained = 0;
        var breaks = 0;
        var forks = 0;
        var relinks = 0;
        var firstBroken = 0L;
        var firstDiagnosis = string.Empty;
        var started = Stopwatch.GetTimestamp();

        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        while (true)
        {
            var batch = 0;
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = SelectRowsSql;
            command.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
            command.Parameters.AddWithValue("toExclusive", month.ToExclusive);
            command.Parameters.AddWithValue("afterSeq", cursor);
            command.Parameters.AddWithValue("maxRows", BatchSize);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    cursor = reader.GetInt64(0);
                    batch++;
                    rows++;
                    if (reader.IsDBNull(1))
                    {
                        continue;
                    }

                    var canonical = reader.GetString(1);
                    var prevHash = reader.GetFieldValue<byte[]>(2);
                    var hash = reader.GetFieldValue<byte[]>(3);
                    chained++;
                    var forked = !prevHash.AsSpan().SequenceEqual(running);
                    var relinked = !AuditChainArithmetic.Link(prevHash, canonical).AsSpan().SequenceEqual(hash);
                    if (forked || relinked)
                    {
                        breaks++;
                        forks += forked ? 1 : 0;
                        relinks += relinked ? 1 : 0;
                        if (firstBroken == 0)
                        {
                            firstBroken = cursor;
                            firstDiagnosis = forked
                                ? $"elo aponta para {Convert.ToHexString(prevHash)[..12]} e o anterior fechou em "
                                    + $"{Convert.ToHexString(running)[..12]}"
                                : "o hash gravado não corresponde ao texto canônico da própria linha";
                        }
                    }

                    running = hash;
                }
            }

            if (batch < BatchSize)
            {
                break;
            }
        }

        return new VerificationCost(
            volume,
            rows,
            chained,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            breaks,
            forks,
            relinks,
            firstBroken,
            firstDiagnosis);
    }
}
