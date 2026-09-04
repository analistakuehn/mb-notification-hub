using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>
/// Scheduler of the sweep of abandoned attachments: one round at host start,
/// then one per configured interval. The round holds no state at all, so a
/// replica that dies mid-round leaves nothing only it knew about, and a failed
/// round is logged and retried on the next tick rather than taking the host
/// down.
/// </summary>
/// <remarks>
/// A round at host start costs one selection and, for each candidate it finds,
/// the same work the next tick would have done. Every removal is guarded by
/// the window, by the row and by the dependencies, so a deploy that restarts
/// this role several times removes nothing it would not have removed anyway.
/// </remarks>
internal sealed class AttachmentAbandonmentService(
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentAbandonmentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AttachmentRetentionOptions settings = options.Value;
        if (!settings.Enabled)
        {
            logger.AttachmentAbandonmentDisabled();
            return;
        }

        logger.AttachmentAbandonmentStarted(settings.Interval, settings.BatchSize);
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
            // Host shutdown. Nothing to flush: an attachment whose content was
            // not removed is still in the state it was in, and the next round
            // finds it exactly where this one left it.
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<AttachmentAbandonmentScan>()
                .RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.AttachmentAbandonmentRoundFailed(exception);
        }
    }
}
