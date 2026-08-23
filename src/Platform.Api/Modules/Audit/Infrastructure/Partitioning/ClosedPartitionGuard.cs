using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// The write-closing steps of a partition, in the only order that is safe:
/// stop the writes, then detach, then, and only under its own gate, destroy.
/// </summary>
/// <remarks>
/// Revoking the insert grant is not enough on its own. A row inserted through
/// the partitioned parent is routed to its partition, and the privilege the
/// route checks is the parent's, so the revoke alone would leave the closed
/// month writable through the front door. The row-level trigger is what
/// actually refuses the write; the revoke removes the direct path to the
/// partition. Both are needed, which is what the accepted immutability design
/// already prescribes.
/// </remarks>
internal sealed class ClosedPartitionGuard(AuditDbContext db, ILogger<ClosedPartitionGuard> logger)
{
    /// <summary>Role the application connects under; it may append and read, never modify.</summary>
    internal const string AppenderRole = "audit_appender";

    /// <summary>Function the closed-partition trigger calls; created by the module migration.</summary>
    internal const string RejectClosedWriteFunction = "audit.reject_closed_partition_write";

    /// <summary>
    /// Grants the appender its rights on a partition that is still open.
    /// Grants do not descend from the partitioned parent to a partition
    /// created later, so an open month has to be granted explicitly; without
    /// it the closing revoke would have nothing to take away and would only
    /// look like it worked.
    /// </summary>
    public async Task EnsureAppenderGrantAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        var partition = SafeName(window);
        var sql = $"""GRANT INSERT, SELECT ON audit."{partition}" TO {AppenderRole}""";
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    /// <summary>Stops every write to a closed partition and reports whether the state changed.</summary>
    public async Task RevokeWritesAsync(MonthlyPartitionWindow window, CancellationToken cancellationToken)
    {
        var partition = SafeName(window);
        var sql = $"""
            REVOKE INSERT ON audit."{partition}" FROM {AppenderRole};
            DROP TRIGGER IF EXISTS {TriggerName(partition)} ON audit."{partition}";
            CREATE TRIGGER {TriggerName(partition)}
                BEFORE INSERT ON audit."{partition}"
                FOR EACH ROW EXECUTE FUNCTION {RejectClosedWriteFunction}();
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.PartitionWritesRevoked(partition);
    }

    /// <summary>Detaches the partition from the trail; the table and its rows stay.</summary>
    public async Task DetachAsync(MonthlyPartitionWindow window, CancellationToken cancellationToken)
    {
        var partition = SafeName(window);
        var sql = $"""ALTER TABLE audit.audit_event DETACH PARTITION audit."{partition}" """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.PartitionDetached(partition);
    }

    /// <summary>Destroys a detached partition. Reachable only behind its own gate.</summary>
    public async Task DropAsync(MonthlyPartitionWindow window, CancellationToken cancellationToken)
    {
        var partition = SafeName(window);
        var sql = $"""DROP TABLE audit."{partition}" """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.PartitionDropped(partition);
    }

    /// <summary>True when the appender role can no longer insert directly into the partition.</summary>
    public async Task<bool> AreWritesRevokedAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        var qualified = $"audit.{SafeName(window)}";
        var granted = await db.Database
            .SqlQuery<bool>(
                $"""SELECT has_table_privilege({AppenderRole}, {qualified}, 'INSERT') AS "Value" """)
            .SingleAsync(cancellationToken);
        return !granted;
    }

    private static string TriggerName(string partition) => $"trg_{partition}_closed";

    private static string SafeName(MonthlyPartitionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return PartitionIdentifiers.IsSafeIdentifier(window.PartitionName)
            ? window.PartitionName
            : throw new InvalidOperationException(
                $"O nome de partição '{window.PartitionName}' não é um identificador PostgreSQL seguro.");
    }
}
