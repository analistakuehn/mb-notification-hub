using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Health of the scheduler inside its role. It answers one question, the only
/// one a stopped scheduler answers wrongly by default: are rounds still
/// landing. A process whose scan loop died stays up, keeps its connections and
/// keeps consuming its queue, so every other probe of this role reports
/// success while fallbacks and releases quietly stop happening.
/// </summary>
internal sealed class SchedulerScanHealthCheck(
    SchedulerScanHeartbeat heartbeat,
    IOptions<SchedulerScanOptions> options) : IHealthCheck
{
    internal const string Name = "notifications-scheduler-scan";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        SchedulerScanOptions settings = options.Value;
        if (!settings.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "A varredura do scheduler está desligada por configuração."));
        }

        (TimeSpan? sinceLastRound, var lastFailure) = heartbeat.Read();
        TimeSpan tolerance = settings.Interval * settings.HealthyRoundsMissedLimit;
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["sinceLastRound"] = sinceLastRound?.ToString() ?? "never",
            ["tolerance"] = tolerance.ToString(),
            ["lastFailure"] = lastFailure ?? "none",
        };

        // No round yet is healthy, not unhealthy: the first round of a host
        // that just started has not had time to land, and reporting a failure
        // there would make every rollout look like an outage.
        if (sinceLastRound is not { } since)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "A varredura do scheduler ainda não concluiu a primeira rodada.", data));
        }

        return Task.FromResult(since <= tolerance
            ? HealthCheckResult.Healthy("A varredura do scheduler está em dia.", data)
            : HealthCheckResult.Unhealthy(
                "A varredura do scheduler parou de concluir rodadas.", data: data));
    }
}
