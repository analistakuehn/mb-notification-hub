using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>
/// Scheduler of the repair round: one round at host start, then one per
/// configured interval. The round holds no state at all, so a replica that
/// dies mid-round leaves nothing only it knew about, and a failed round is
/// logged and retried on the next tick rather than taking the host down.
/// </summary>
/// <remarks>
/// A round at host start is deliberate. Every repair is guarded by the word
/// written on the row and by the record of generations, no message reaches
/// anybody, and the only thing that leaves is a removal of bytes the record
/// does not account for, so a deploy that restarts this role several times
/// costs a few listings and nothing else.
/// </remarks>
internal sealed class AttachmentReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AttachmentReconciliationOptions settings = options.Value;
        if (!settings.Enabled)
        {
            logger.AttachmentReconciliationDisabled();
            return;
        }

        logger.AttachmentReconciliationStarted(settings.Interval, settings.BatchSize);
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
            // Host shutdown. Nothing to flush: every repair settles on its own
            // and whatever this round did not reach is still recorded on the
            // rows for the next one.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<AttachmentReconciliationScan>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.AttachmentReconciliationRoundFailed(exception);
        }
    }
}
