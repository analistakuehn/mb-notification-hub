using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// One maintenance round over the module's partitioned tables: delegates the
/// idempotent monthly provisioning to the platform partitioning
/// infrastructure over the module's schema and tables. The revoke and
/// retention steps are closing semantics of the trail, stay in this module,
/// and remain declared behind configuration gates until the later phase
/// provisions the database roles and the WORM bucket they require.
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
        var provisioner = new MonthlyPartitionProvisioner(db.Database, Schema, timeProvider, logger);
        var created = 0;

        foreach (var table in EffectiveTables(value))
        {
            created += await provisioner.EnsureMonthlyPartitionsAsync(
                table, value.MonthsAhead, cancellationToken);
        }

        ReportGatedSteps(value);
        return created;
    }

    /// <summary>The configured tables, deduplicated; the module default when none is configured.</summary>
    internal static IReadOnlyList<string> EffectiveTables(PartitionManagerOptions value)
        => value.PartitionedTables.Count == 0
            ? DefaultPartitionedTables
            : value.PartitionedTables.Distinct(StringComparer.Ordinal).ToList();

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
