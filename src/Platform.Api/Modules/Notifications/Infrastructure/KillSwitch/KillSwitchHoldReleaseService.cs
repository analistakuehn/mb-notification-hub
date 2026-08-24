namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal sealed class KillSwitchHoldReleaseService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<KillSwitchHoldReleaseService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<KillSwitchHoldReleaser>()
                    .ReleaseBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.KillSwitchReleaseFailed(exception);
            }

            await Task.Delay(Interval, timeProvider, stoppingToken);
        }
    }
}
