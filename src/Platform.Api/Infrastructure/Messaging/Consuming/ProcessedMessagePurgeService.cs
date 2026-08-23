using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Scheduler of the processed-messages purge: one round at host start, then
/// one round per configured interval. A failed round is logged and retried on
/// the next tick; the job never brings the host down over a purge error.
/// </summary>
internal sealed class ProcessedMessagePurgeService(
    IServiceScopeFactory scopeFactory,
    IOptions<ProcessedMessagePurgeOptions> options,
    ILogger<ProcessedMessagePurgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.ProcessedMessagePurgeDisabled();
            return;
        }

        logger.ProcessedMessagePurgeStarted(options.Value.Interval, options.Value.Retention);
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
                .GetRequiredService<ProcessedMessagePurge>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.ProcessedMessagePurgeRoundFailed(exception);
        }
    }
}
