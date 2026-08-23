using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationHub.Api.Infrastructure.Partitioning;

/// <summary>
/// Degrades the host health when the contiguous future partition coverage of
/// a monthly-partitioned table ends in less than the configured number of
/// days. A missing month makes every insert of that month fail, so operators
/// need the warning while there is still time to provision the next
/// partitions.
/// </summary>
internal sealed class PartitionCoverageHealthCheck(
    DatabaseFacade database,
    string schema,
    string table,
    int minimumFutureDays,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<string> partitions = await database
                .SqlQuery<string>(
                    $"""
                     SELECT child.relname AS "Value"
                     FROM pg_inherits
                     JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                     JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                     JOIN pg_namespace parent_schema ON parent_schema.oid = parent.relnamespace
                     WHERE parent_schema.nspname = {schema} AND parent.relname = {table}
                     """)
                .ToListAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            DateOnly coverageEnd = ContiguousCoverageEnd(partitions, table, now);
            var daysLeft = (coverageEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now.UtcDateTime).TotalDays;

            return daysLeft < minimumFutureDays
                ? HealthCheckResult.Degraded(
                    $"A cobertura contígua de partições de {table} termina em {coverageEnd:yyyy-MM-dd} "
                    + $"({daysLeft:F0} dias); o mínimo configurado é {minimumFutureDays} dias.")
                : HealthCheckResult.Healthy(
                    $"A cobertura de partições de {table} vai até {coverageEnd:yyyy-MM-dd} ({daysLeft:F0} dias).");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable database already degrades the host through the
            // persistence checks; reporting Degraded (not Unhealthy) keeps
            // this check advisory.
            return HealthCheckResult.Degraded(
                $"Não foi possível verificar a cobertura de partições de {table}.",
                exception);
        }
    }

    /// <summary>
    /// Exclusive upper boundary of the contiguous monthly coverage starting at
    /// the current month: the first month without a partition. A hole in the
    /// middle counts as the end, because inserts in that month would fail even
    /// with later partitions present.
    /// </summary>
    private static DateOnly ContiguousCoverageEnd(
        List<string> partitions,
        string table,
        DateTimeOffset now)
    {
        var names = new HashSet<string>(partitions, StringComparer.Ordinal);
        var month = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        while (names.Contains($"{table}_{month.Year:D4}_{month.Month:D2}"))
        {
            month = month.AddMonths(1);
        }

        return month;
    }
}
