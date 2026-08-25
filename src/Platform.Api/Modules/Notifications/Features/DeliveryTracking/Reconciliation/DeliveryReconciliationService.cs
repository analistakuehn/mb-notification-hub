using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>
/// Scheduler of the reconciliation: one round at host start, then one per
/// configured interval. The round holds no state at all, so a replica that
/// dies mid-round leaves nothing only it knew about, and a failed round is
/// logged and retried on the next tick rather than taking the host down: the
/// rows it corrects are already past every deadline that mattered.
/// </summary>
/// <remarks>
/// A round at host start is deliberate and safe on this job. Every provider
/// call is a read, every write is guarded by the identity of the provider
/// event, and no message is ever sent to anybody, so a deploy that restarts
/// this role several times costs a few repeated reads and nothing else.
/// </remarks>
internal sealed class DeliveryReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliveryReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DeliveryReconciliationOptions settings = options.Value;
        if (!settings.Enabled)
        {
            logger.ReconciliationDisabled();
            return;
        }

        logger.ReconciliationStarted(settings.Interval, settings.StaleAfter, settings.BatchSize);
        try
        {
            using var timer = new PeriodicTimer(settings.Interval, timeProvider);
            do
            {
                await RunRoundAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown. Nothing to flush: every answer commits on its own,
            // and whatever this round did not reach is still in the database
            // for the next one.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<DeliveryReconciliationScan>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.ReconciliationRoundFailed(exception);
        }
    }
}
