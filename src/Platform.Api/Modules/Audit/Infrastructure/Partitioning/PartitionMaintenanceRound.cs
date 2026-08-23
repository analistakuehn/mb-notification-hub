namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// One maintenance round, serialized across instances. The round detaches
/// partitions and writes evidence, so two instances running it at once could
/// have one detach what the other is still exporting. An instance that does
/// not get the lock skips the round; the next cadence covers the same ground.
/// </summary>
internal sealed class PartitionMaintenanceRound(
    AuditMaintenanceLock maintenanceLock,
    PartitionMaintenance maintenance)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable? handle = await maintenanceLock.TryAcquireAsync(
            AuditMaintenanceLock.PartitionRoundKey, cancellationToken);
        return handle is null ? 0 : await maintenance.RunAsync(cancellationToken);
    }
}
