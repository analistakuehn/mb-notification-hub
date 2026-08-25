using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;

/// <summary>
/// Keeps the monthly partitions of this module's partitioned tables
/// provisioned ahead of time: one idempotent round at host start, then one
/// per configured interval, delegating the mechanics to the platform
/// partitioning infrastructure over this module's schema. A failed round is
/// logged and retried on the next tick; the job never brings the host down.
/// </summary>
internal sealed class NotificationsPartitionManagerService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsPartitionOptions> options,
    ILogger<NotificationsPartitionManagerService> logger) : BackgroundService
{
    private const string Schema = "notifications";

    /// <summary>Every partitioned table this module owns, provisioned together.</summary>
    internal static readonly string[] PartitionedTables =
        [
        "notification",
        "notification_attempt",
        "policy_evaluation",
        "delivery_event",
        "delivery_payload",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.NotificationPartitionManagerDisabled();
            return;
        }

        logger.NotificationPartitionManagerStarted(options.Value.Interval);
        try
        {
            await RunRoundAsync(stoppingToken);
            using var timer = new PeriodicTimer(options.Value.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunRoundAsync(stoppingToken);
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
            NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            TimeProvider timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            var provisioner = new MonthlyPartitionProvisioner(db.Database, Schema, timeProvider, logger);
            foreach (var table in PartitionedTables) await provisioner.EnsureMonthlyPartitionsAsync(table, options.Value.MonthsAhead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.NotificationPartitionRoundFailed(exception);
        }
    }
}
