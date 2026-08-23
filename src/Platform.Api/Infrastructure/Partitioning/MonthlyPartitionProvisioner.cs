using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NotificationHub.Api.Infrastructure.Partitioning;

/// <summary>
/// Idempotent provisioning of monthly partitions over one schema: guarantees
/// the windows from the current UTC month through the requested horizon.
/// Creation uses IF NOT EXISTS over deterministic names, so provisioning the
/// same window twice neither fails nor duplicates anything. The consuming
/// module supplies the schema, the tables, and the logger; partition-closing
/// steps such as write revokes or retention stay with the module that owns
/// the data.
/// </summary>
internal sealed class MonthlyPartitionProvisioner(
    DatabaseFacade database,
    string schema,
    TimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>
    /// Guarantees the monthly partitions of <paramref name="table"/> from the
    /// current UTC month through <paramref name="monthsAhead"/> months after
    /// it, inclusive; returns how many partitions were created.
    /// </summary>
    public async Task<int> EnsureMonthlyPartitionsAsync(
        string table,
        int monthsAhead,
        CancellationToken cancellationToken)
    {
        if (!PartitionIdentifiers.IsSafeIdentifier(schema))
        {
            throw new InvalidOperationException(
                $"Schema name '{schema}' is not a safe unquoted PostgreSQL identifier.");
        }

        if (!PartitionIdentifiers.IsSafeIdentifier(table))
        {
            throw new InvalidOperationException(
                $"Table name '{table}' is not a safe unquoted PostgreSQL identifier.");
        }

        if (!await IsPartitionedParentAsync(table, cancellationToken))
        {
            logger.TableIsNotAPartitionedParent(table);
            return 0;
        }

        var created = 0;
        foreach (MonthlyPartitionWindow window in MonthlyPartitions.Plan(
            table, timeProvider.GetUtcNow(), monthsAhead))
        {
            if (await PartitionExistsAsync(window.PartitionName, cancellationToken))
            {
                logger.PartitionAlreadyExists(window.PartitionName, table);
                continue;
            }

            var sql = BuildCreatePartitionSql(schema, table, window);
            await database.ExecuteSqlRawAsync(sql, cancellationToken);
            logger.PartitionCreated(window.PartitionName, table);
            created++;
        }

        return created;
    }

    private async Task<bool> IsPartitionedParentAsync(string table, CancellationToken cancellationToken)
        => await database
            .SqlQuery<bool>(
                $"""
                 SELECT EXISTS (
                     SELECT 1
                     FROM pg_partitioned_table
                     WHERE partrelid = to_regclass({schema + "." + table})
                 ) AS "Value"
                 """)
            .SingleAsync(cancellationToken);

    private async Task<bool> PartitionExistsAsync(string partitionName, CancellationToken cancellationToken)
        => await database
            .SqlQuery<bool>(
                $"""SELECT to_regclass({schema + "." + partitionName}) IS NOT NULL AS "Value" """)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// DDL with identifiers validated against the safe-identifier rule above;
    /// PostgreSQL does not accept parameters in identifier or bound positions
    /// of a CREATE TABLE statement.
    /// </summary>
    private static string BuildCreatePartitionSql(
        string schema,
        string table,
        MonthlyPartitionWindow window)
    {
        var from = window.FromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = window.ToExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"""
                CREATE TABLE IF NOT EXISTS {schema}."{window.PartitionName}"
                PARTITION OF {schema}."{table}"
                FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00')
                """;
    }
}
