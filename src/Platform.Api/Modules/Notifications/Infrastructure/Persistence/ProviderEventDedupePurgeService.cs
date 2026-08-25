using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Scheduler of the provider deduplication purge: one round at host start,
/// then one round per configured interval. A failed round is logged and
/// retried on the next tick; the job never brings the host down over a purge
/// error. Every round is idempotent, so hosting it on a role with several
/// replicas costs a repeated delete of nothing.
/// </summary>
internal sealed class ProviderEventDedupePurgeService(
    IServiceScopeFactory scopeFactory,
    IOptions<ProviderEventDedupePurgeOptions> options,
    ILogger<ProviderEventDedupePurgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.ProviderEventDedupePurgeDisabled();
            return;
        }

        logger.ProviderEventDedupePurgeStarted(options.Value.Interval, options.Value.Retention);
        try
        {
            await RunRoundAsync(stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: nothing to flush, the round is idempotent.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<ProviderEventDedupePurge>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.ProviderEventDedupePurgeRoundFailed(exception);
        }
    }
}
