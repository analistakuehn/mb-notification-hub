using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// Scheduler of the chain verification: one round at host start, then one per
/// configured cadence. A failed round is logged and retried on the next tick;
/// the sensor never brings the host down, and the health check is what reports
/// that rounds stopped landing.
/// </summary>
internal sealed class ChainVerificationService(
    IServiceScopeFactory scopeFactory,
    IOptions<ChainVerificationOptions> options,
    ILogger<ChainVerificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.ChainVerificationDisabled();
            return;
        }

        logger.ChainVerificationStarted(options.Value.Interval);
        try
        {
            await RunRoundAsync(stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunRoundAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: the round is idempotent and resumes at the checkpoint.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<ChainVerificationRound>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.ChainVerificationRoundFailed(exception);
        }
    }
}
