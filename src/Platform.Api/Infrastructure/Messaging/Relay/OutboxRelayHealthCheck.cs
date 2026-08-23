using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>Pending aggregate of one destination as read from the outbox.</summary>
internal sealed class PendingDestinationRow
{
    public string Transport { get; set; } = string.Empty;

    public string Destination { get; set; } = string.Empty;

    public long PendingCount { get; set; }

    public DateTimeOffset OldestCreatedAt { get; set; }
}

/// <summary>
/// Degrades the worker when a destination has no queue or topic, and always
/// exposes the pending backlog per transport and destination (count and oldest
/// age) as health data. The transport dimension is what makes a lane this
/// instance does not drain visible instead of silent: rows addressed to a bus
/// that is not composed here accumulate under their own transport key.
/// </summary>
internal sealed class OutboxRelayHealthCheck(
    PlatformMessagingDbContext db,
    OutboxRelayHealthState healthState,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<PendingDestinationRow> pending = await db.Database
                .SqlQuery<PendingDestinationRow>(
                    $"""
                     SELECT transport AS "Transport",
                            destination AS "Destination",
                            count(*) AS "PendingCount",
                            min(created_at) AS "OldestCreatedAt"
                     FROM platform.outbox
                     WHERE sent_at IS NULL
                     GROUP BY transport, destination
                     """)
                .ToListAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            var data = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (PendingDestinationRow row in pending)
            {
                data[$"pending-count:{row.Transport}:{row.Destination}"] = row.PendingCount;
                data[$"pending-oldest-age-seconds:{row.Transport}:{row.Destination}"] =
                    Math.Max(0, (now - row.OldestCreatedAt).TotalSeconds);
            }

            var missing = healthState.MissingQueues.Keys.Order(StringComparer.Ordinal).ToArray();
            return missing.Length > 0
                ? HealthCheckResult.Degraded(
                    $"Destinos sem fila ou tópico: {string.Join(", ", missing)}. "
                    + "As mensagens permanecem pendentes; o relay nunca cria fila nem tópico.",
                    data: data)
                : HealthCheckResult.Healthy("Relay do outbox operante.", data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable database already degrades through the persistence
            // checks; this check stays advisory.
            return HealthCheckResult.Degraded(
                "Não foi possível inspecionar o backlog pendente do outbox.",
                exception);
        }
    }
}
