using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// Degrades the module health when the contiguous future partition coverage of
/// the audit table ends in less than the configured number of days. A missing
/// month makes every audit insert fail, which aborts every governed effect in
/// the same transaction, so operators need the warning while there is still
/// time to provision the next partitions.
/// </summary>
internal sealed class AuditPartitionCoverageHealthCheck(
    AuditDbContext db,
    IOptions<PartitionManagerOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private const string Schema = "audit";
    private const string Table = "audit_event";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<string> partitions = await db.Database
                .SqlQuery<string>(
                    $"""
                     SELECT child.relname AS "Value"
                     FROM pg_inherits
                     JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                     JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
                     JOIN pg_namespace parent_schema ON parent_schema.oid = parent.relnamespace
                     WHERE parent_schema.nspname = {Schema} AND parent.relname = {Table}
                     """)
                .ToListAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            DateOnly coverageEnd = ContiguousCoverageEnd(partitions, now);
            var daysLeft = (coverageEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now.UtcDateTime).TotalDays;
            var minimumDays = options.Value.FutureWindowMinimumDays;

            return daysLeft < minimumDays
                ? HealthCheckResult.Degraded(
                    $"A cobertura contígua de partições de {Table} termina em {coverageEnd:yyyy-MM-dd} "
                    + $"({daysLeft:F0} dias); o mínimo configurado é {minimumDays} dias.")
                : HealthCheckResult.Healthy(
                    $"A cobertura de partições de {Table} vai até {coverageEnd:yyyy-MM-dd} ({daysLeft:F0} dias).");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable database already degrades the module; reporting
            // Degraded (not Unhealthy) keeps this check advisory.
            return HealthCheckResult.Degraded(
                $"Não foi possível verificar a cobertura de partições de {Table}.",
                exception);
        }
    }

    /// <summary>
    /// Exclusive upper boundary of the contiguous monthly coverage starting at
    /// the current month: the first month without a partition. A hole in the
    /// middle counts as the end, because inserts in that month would fail even
    /// with later partitions present.
    /// </summary>
    private static DateOnly ContiguousCoverageEnd(List<string> partitions, DateTimeOffset now)
    {
        var names = new HashSet<string>(partitions, StringComparer.Ordinal);
        var month = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        while (names.Contains($"{Table}_{month.Year:D4}_{month.Month:D2}"))
        {
            month = month.AddMonths(1);
        }

        return month;
    }
}
