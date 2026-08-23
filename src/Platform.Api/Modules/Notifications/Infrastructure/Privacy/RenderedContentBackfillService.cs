using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// Runs the backfill once at host start when its gate is on, then stops. One
/// pass per start on purpose: rewriting stored ciphertext is an operation an
/// operator decides to run and then watches, never a loop that keeps touching
/// governed rows on its own.
/// </summary>
internal sealed class RenderedContentBackfillService(
    IServiceScopeFactory scopeFactory,
    IOptions<RenderedContentBackfillOptions> options,
    ILogger<RenderedContentBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.RenderedContentBackfillDisabled();
            return;
        }

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<RenderedContentBackfill>()
                .RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: every substitution already committed on its own.
        }
        catch (Exception exception)
        {
            logger.RenderedContentBackfillFailed(exception);
        }
    }
}
