using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// Scheduler of the rendered-content sweep: one round at host start, then one
/// round per configured interval. A failed round is logged and retried on the
/// next tick; the job never brings the host down over a sweep error, because
/// the rows it settles are already past every deadline that mattered.
/// </summary>
internal sealed class RenderedContentSweepService(
    IServiceScopeFactory scopeFactory,
    IOptions<RenderedContentRetentionOptions> options,
    ILogger<RenderedContentSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.RenderedContentSweepDisabled();
            return;
        }

        logger.RenderedContentSweepStarted(options.Value.Interval, options.Value.Grace);
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
            // Host shutdown: nothing to flush, the round is idempotent.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<RenderedContentSweep>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.RenderedContentSweepRoundFailed(exception);
        }
    }
}
