using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal sealed class KillSwitchHealthCheck(KillSwitchCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        KillSwitchSnapshotStatus status = await cache.EnsureAvailableAsync(cancellationToken);
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["snapshot"] = status.State.ToString().ToLowerInvariant(),
        };
        HealthCheckResult result = status.State == KillSwitchSnapshotState.Fresh
            ? HealthCheckResult.Healthy("Snapshot do kill switch está vigente.", data)
            : HealthCheckResult.Unhealthy(
                "Snapshot do kill switch não está disponível.",
                data: data);
        return result;
    }
}
