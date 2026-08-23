using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// One maintenance round over the module's partitioned tables: guarantees the
/// monthly partitions from the current month through the configured horizon.
/// Creation uses IF NOT EXISTS over deterministic names, so running the round
/// twice neither fails nor duplicates anything. The revoke and retention steps
/// are declared behind configuration gates and stay inactive until the later
/// phase provisions the database roles and the WORM bucket they require.
/// </summary>
internal sealed class PartitionMaintenance(
    AuditDbContext db,
    IOptions<PartitionManagerOptions> options,
    TimeProvider timeProvider,
    ILogger<PartitionMaintenance> logger)
{
    private const string Schema = "audit";

    /// <summary>
    /// Tables maintained when configuration declares none. The default lives
    /// on the consumer side so a configured list fully replaces it: binding
    /// would append to a non-empty default and make entries irremovable.
    /// </summary>
    private static readonly string[] DefaultPartitionedTables = ["audit_event"];

    /// <summary>Runs one round and returns how many partitions were created.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        PartitionManagerOptions value = options.Value;
        var created = 0;

        foreach (var table in EffectiveTables(value))
        {
            created += await EnsureMonthlyPartitionsAsync(table, value.MonthsAhead, cancellationToken);
        }

        ReportGatedSteps(value);
        return created;
    }

    /// <summary>The configured tables, deduplicated; the module default when none is configured.</summary>
    internal static IReadOnlyList<string> EffectiveTables(PartitionManagerOptions value)
        => value.PartitionedTables.Count == 0
            ? DefaultPartitionedTables
            : value.PartitionedTables.Distinct(StringComparer.Ordinal).ToList();

    private async Task<int> EnsureMonthlyPartitionsAsync(
        string table,
        int monthsAhead,
        CancellationToken cancellationToken)
    {
        if (!PartitionManagerOptions.IsSafeTableName(table))
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

            var sql = BuildCreatePartitionSql(table, window);
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            logger.PartitionCreated(window.PartitionName, table);
            created++;
        }

        return created;
    }

    private async Task<bool> IsPartitionedParentAsync(string table, CancellationToken cancellationToken)
        => await db.Database
            .SqlQuery<bool>(
                $"""
                 SELECT EXISTS (
                     SELECT 1
                     FROM pg_partitioned_table
                     WHERE partrelid = to_regclass({Schema + "." + table})
                 ) AS "Value"
                 """)
            .SingleAsync(cancellationToken);

    private async Task<bool> PartitionExistsAsync(string partitionName, CancellationToken cancellationToken)
        => await db.Database
            .SqlQuery<bool>(
                $"""SELECT to_regclass({Schema + "." + partitionName}) IS NOT NULL AS "Value" """)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// DDL with identifiers validated against the safe-name rule above;
    /// PostgreSQL does not accept parameters in identifier or bound positions
    /// of a CREATE TABLE statement.
    /// </summary>
    private static string BuildCreatePartitionSql(string table, MonthlyPartitionWindow window)
    {
        var from = window.FromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = window.ToExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"""
                CREATE TABLE IF NOT EXISTS {Schema}."{window.PartitionName}"
                PARTITION OF {Schema}."{table}"
                FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00')
                """;
    }

    private void ReportGatedSteps(PartitionManagerOptions value)
    {
        if (value.EnableRevokeOnClosedPartitions)
        {
            logger.RevokeStepEnabledButUnavailable();
        }
        else
        {
            logger.RevokeStepInactive();
        }

        if (value.EnableRetentionCycle)
        {
            logger.RetentionCycleEnabledButUnavailable();
        }
        else
        {
            logger.RetentionCycleInactive();
        }
    }
}
