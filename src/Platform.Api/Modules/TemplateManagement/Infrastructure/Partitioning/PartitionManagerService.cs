using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

/// <summary>
/// Scheduler of the partition maintenance: one round at host start, then one
/// round per configured interval. A failed round is logged and retried on the
/// next tick; the job never brings the host down over a maintenance error.
/// </summary>
internal sealed class PartitionManagerService(
    IServiceScopeFactory scopeFactory,
    IOptions<PartitionManagerOptions> options,
    ILogger<PartitionManagerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.PartitionManagerDisabled();
            return;
        }

        logger.PartitionManagerStarted(options.Value.Interval);
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
                .GetRequiredService<PartitionMaintenance>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.PartitionMaintenanceRoundFailed(exception);
        }
    }
}
