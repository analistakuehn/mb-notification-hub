using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Hosts the relay loop: one scope per pass, immediate re-loop while a pass
/// publishes cleanly, and one poll interval of rest when the outbox is empty
/// or a destination is failing. A failed pass never stops the service; the
/// rows are still in the outbox and the next pass starts from them.
/// </summary>
internal sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRelayOptions> options,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bands = options.Value.Bands.Length == 0 ? "todas" : string.Join(",", options.Value.Bands);
        logger.OutboxRelayStarted(options.Value.PollInterval, options.Value.BatchSize, bands);
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                OutboxRelayPassResult result = await RunPassAsync(stoppingToken);
                if (result.Published == 0 || result.Failed > 0)
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: pending rows stay pending; the next start resumes.
        }
    }

    private async Task<OutboxRelayPassResult> RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<OutboxRelay>()
                .RunPassAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.OutboxRelayPassFailed(exception);
            return OutboxRelayPassResult.None;
        }
    }
}
