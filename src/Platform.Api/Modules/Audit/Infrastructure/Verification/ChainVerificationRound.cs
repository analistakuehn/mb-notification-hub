using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// One verification round, serialized across instances. Two hosts verifying at
/// once would duplicate the audit events of every round and race on the
/// checkpoints; the round simply steps aside when another instance holds the
/// lock, because the next cadence covers the same ground.
/// </summary>
internal sealed class ChainVerificationRound(
    AuditMaintenanceLock maintenanceLock,
    ChainVerifier verifier)
{
    public async Task<IReadOnlyList<ChainVerificationOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable? handle = await maintenanceLock.TryAcquireAsync(
            AuditMaintenanceLock.VerificationRoundKey, cancellationToken);
        return handle is null ? [] : await verifier.RunAsync(cancellationToken);
    }
}
