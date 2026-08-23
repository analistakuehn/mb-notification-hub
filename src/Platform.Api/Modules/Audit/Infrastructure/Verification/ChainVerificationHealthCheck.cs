using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// Operational exposure of the integrity sensor. Two conditions degrade the
/// host: a partition whose chain did not verify, and rounds that stopped
/// landing. The second matters as much as the first, because a verifier that
/// silently stopped running looks exactly like a trail that is never tampered
/// with.
/// </summary>
internal sealed class ChainVerificationHealthCheck(
    AuditDbContext db,
    TimeSpan staleAfter,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<ChainVerificationCheckpoint> checkpoints = await db.ChainVerificationCheckpoints
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            // Every broken partition is named, not just the first: an operator
            // reading one name would fix it and believe the trail is whole.
            var broken = checkpoints
                .Where(entry => entry.Failure is not null)
                .Select(entry => $"{entry.PartitionName} ({entry.Failure}, seq {entry.FailedSeq})")
                .ToArray();
            if (broken.Length > 0)
            {
                return HealthCheckResult.Degraded(
                    $"Verificação de cadeia falhou em {broken.Length} partição(ões): {string.Join("; ", broken)}.");
            }

            if (checkpoints.Count == 0)
            {
                return HealthCheckResult.Degraded(
                    "Nenhuma rodada de verificação de cadeia foi registrada até agora.");
            }

            DateTimeOffset newest = checkpoints.Max(entry => entry.VerifiedAt);
            DateTimeOffset now = timeProvider.GetUtcNow();
            return now - newest > staleAfter
                ? HealthCheckResult.Degraded(
                    $"A última verificação de cadeia ocorreu em {newest:yyyy-MM-dd HH:mm:ss}Z, "
                    + $"acima da tolerância de {staleAfter}.")
                : HealthCheckResult.Healthy(
                    $"Cadeia verificada em {checkpoints.Count} partição(ões); a mais recente em "
                    + $"{newest:yyyy-MM-dd HH:mm:ss}Z.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable database already degrades the host through the
            // persistence checks; reporting Degraded keeps this check advisory.
            return HealthCheckResult.Degraded(
                "Não foi possível consultar o estado da verificação de cadeia.", exception);
        }
    }
}
