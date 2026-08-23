using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// What the database actually holds, as opposed to what the plan expects: the
/// partitions still attached to the trail, and the ones already detached and
/// waiting for a retention decision. The closing cycle and the verification
/// both work from this, never from a list held in configuration, because the
/// database is the only place that knows what survived the last round.
/// </summary>
internal sealed class AuditPartitionCatalog(AuditDbContext db)
{
    internal const string Schema = "audit";

    internal const string Table = "audit_event";

    /// <summary>Partitions currently attached to the trail, oldest month first.</summary>
    internal async Task<IReadOnlyList<MonthlyPartitionWindow>> AttachedAsync(CancellationToken cancellationToken)
    {
        List<string> names = await db.Database
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

        return Windows(names);
    }

    /// <summary>
    /// Tables that carry a partition name but hang from no parent: partitions
    /// already detached by a closing cycle, still holding their rows until a
    /// retention decision removes them.
    /// </summary>
    internal async Task<IReadOnlyList<MonthlyPartitionWindow>> DetachedAsync(CancellationToken cancellationToken)
    {
        var prefix = Table + "\\_%";
        List<string> names = await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT candidate.relname AS "Value"
                 FROM pg_class candidate
                 JOIN pg_namespace candidate_schema ON candidate_schema.oid = candidate.relnamespace
                 WHERE candidate_schema.nspname = {Schema}
                   AND candidate.relkind = 'r'
                   AND candidate.relname LIKE {prefix}
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_inherits WHERE pg_inherits.inhrelid = candidate.oid)
                 """)
            .ToListAsync(cancellationToken);

        return Windows(names);
    }

    /// <summary>
    /// The month a partition name denotes. The name is the contract between
    /// the provisioner, the chain anchor and the export keys, so it is parsed
    /// here rather than guessed anywhere else.
    /// </summary>
    internal static MonthlyPartitionWindow? ParseName(string partitionName)
    {
        if (!partitionName.StartsWith(Table + "_", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = partitionName.Split('_');
        if (parts.Length < 4
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            return null;
        }

        var from = new DateOnly(year, month, 1);
        return new MonthlyPartitionWindow(partitionName, from, from.AddMonths(1));
    }

    private static List<MonthlyPartitionWindow> Windows(List<string> names)
        => [.. names
            .Select(ParseName)
            .OfType<MonthlyPartitionWindow>()
            .OrderBy(window => window.FromInclusive)];
}
