using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Runs one round of every scan per configured interval: the fallback
/// deadlines that elapsed, the sends parked on an inconclusive verdict, the
/// notifications whose release instant has passed, and the suppression signals
/// the delivery applier could not hand to the contact ledger.
/// <para>
/// The loop holds no state at all. Everything a round needs to know is read
/// from the database when the round runs, which is what lets this role run on
/// more than one replica and what lets a replica die mid-round without leaving
/// work that only it knew about. A failed round is logged and retried on the
/// next tick; a scan error never brings the host down, because a scheduler
/// that exits is worse than a scheduler that retries.
/// </para>
/// </summary>
internal sealed class SchedulerScanService(
    IServiceScopeFactory scopeFactory,
    SchedulerScanHeartbeat heartbeat,
    IOptions<SchedulerScanOptions> options,
    TimeProvider timeProvider,
    ILogger<SchedulerScanService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SchedulerScanOptions settings = options.Value;
        if (!settings.Enabled)
        {
            logger.SchedulerScanDisabled();
            return;
        }

        logger.SchedulerScanStarted(settings.Interval, settings.BatchSize, settings.UnknownGrace);
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
            // Host shutdown. Nothing to flush: every round commits its own
            // work, and whatever this round did not claim is still in the
            // database for the next replica to find.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<OverdueFallbackScan>()
                .RunAsync(cancellationToken);
            await scope.ServiceProvider
                .GetRequiredService<DeferredReleaseScan>()
                .RunAsync(cancellationToken);

            // The suppression signals the applier could not hand to the contact
            // ledger. It runs on this interval and not on the reconciliation's
            // because the signal it carries is a destination that must stop
            // being addressed, and a day of retry delay is a day of messages
            // sent to an address the provider already refused.
            await scope.ServiceProvider
                .GetRequiredService<PendingSuppressionDrain>()
                .RunAsync(cancellationToken);
            heartbeat.RoundCompleted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            heartbeat.RoundFailed(exception.GetType().Name);
            logger.SchedulerScanRoundFailed(exception);
        }
    }
}
