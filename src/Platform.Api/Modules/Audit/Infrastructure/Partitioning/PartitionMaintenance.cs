using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// One maintenance round over the module's partitioned tables: provision the
/// months ahead, export the stabilized days of the open partitions, and run
/// the closing cycle over the partitions whose month is over. Provisioning
/// comes first on purpose, because a missing partition makes every audited
/// effect fail, and that outranks any evidence work in the same round.
/// </summary>
internal sealed class PartitionMaintenance(
    AuditDbContext db,
    AuditPartitionCatalog catalog,
    AuditExportPlanner exportPlanner,
    PartitionClosingCycle closingCycle,
    IOptions<PartitionManagerOptions> options,
    TimeProvider timeProvider,
    ILogger<PartitionMaintenance> logger)
{
    private const string Schema = AuditPartitionCatalog.Schema;

    /// <summary>
    /// Tables maintained when configuration declares none. The default lives
    /// on the consumer side so a configured list fully replaces it: binding
    /// would append to a non-empty default and make entries irremovable.
    /// </summary>
    private static readonly string[] DefaultPartitionedTables = [AuditPartitionCatalog.Table];

    /// <summary>Runs one round and returns how many partitions were created.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        PartitionManagerOptions value = options.Value;
        var provisioner = new MonthlyPartitionProvisioner(db.Database, Schema, timeProvider, logger);
        var created = 0;

        foreach (var table in EffectiveTables(value))
        {
            created += await provisioner.EnsureMonthlyPartitionsAsync(
                table, value.MonthsAhead, cancellationToken);
        }

        IReadOnlyList<MonthlyPartitionWindow> attached = await catalog.AttachedAsync(cancellationToken);
        await RunExportAsync(attached, cancellationToken);
        await closingCycle.RunAsync(
            attached, await catalog.DetachedAsync(cancellationToken), cancellationToken);
        return created;
    }

    /// <summary>The configured tables, deduplicated; the module default when none is configured.</summary>
    internal static IReadOnlyList<string> EffectiveTables(PartitionManagerOptions value)
        => value.PartitionedTables.Count == 0
            ? DefaultPartitionedTables
            : value.PartitionedTables.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Exporting is evidence work: a failure must not take the provisioning
    /// down with it, and it must not let the closing cycle believe the
    /// evidence is in place either, which is why the closing cycle verifies
    /// its own copy rather than trusting this step.
    /// </summary>
    private async Task RunExportAsync(
        IReadOnlyList<MonthlyPartitionWindow> attached,
        CancellationToken cancellationToken)
    {
        try
        {
            await exportPlanner.RunDailyAsync(attached, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.ExportFailed(AuditPartitionCatalog.Table, exception);
        }
    }
}
