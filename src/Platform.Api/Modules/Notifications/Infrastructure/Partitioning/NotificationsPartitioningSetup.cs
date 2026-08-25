using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;

public static class NotificationsPartitioningSetup
{
    public static IServiceCollection AddNotificationsPartitioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<NotificationsPartitionOptions>()
            .Bind(configuration.GetSection(NotificationsPartitionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Interval >= TimeSpan.FromMinutes(1),
                "O intervalo do gerenciador de partições deve ser de pelo menos um minuto.")
            .Validate(
                options => options.Interval <= TimeSpan.FromDays(30),
                "O intervalo do gerenciador de partições deve ser de no máximo trinta dias; acima disso a provisão mensal perde rodadas.")
            .ValidateOnStart();

        services.AddHostedService<NotificationsPartitionManagerService>();
        services.AddHealthChecks()
            .AddMonthlyPartitionCoverageCheck<NotificationsDbContext>(
                name: "notifications-partitions",
                schema: "notifications",
                table: "notification",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<NotificationsPartitionOptions>>()
                    .Value.FutureWindowMinimumDays)
            .AddMonthlyPartitionCoverageCheck<NotificationsDbContext>(
                name: "notifications-attempt-partitions",
                schema: "notifications",
                table: "notification_attempt",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<NotificationsPartitionOptions>>()
                    .Value.FutureWindowMinimumDays)
            .AddMonthlyPartitionCoverageCheck<NotificationsDbContext>(
                name: "notifications-policy-evaluation-partitions",
                schema: "notifications",
                table: "policy_evaluation",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<NotificationsPartitionOptions>>()
                    .Value.FutureWindowMinimumDays)
            // Delivery feedback is the one partitioned table this module does
            // not control the arrival rate of: the provider decides when it
            // calls, so a missing future partition refuses evidence the hub
            // has no right to refuse.
            .AddMonthlyPartitionCoverageCheck<NotificationsDbContext>(
                name: "notifications-delivery-event-partitions",
                schema: "notifications",
                table: "delivery_event",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<NotificationsPartitionOptions>>()
                    .Value.FutureWindowMinimumDays)

            // The payload table shares the callback's arrival rate and its
            // partition column, so a gap here refuses exactly the same evidence
            // a gap in delivery_event would.
            .AddMonthlyPartitionCoverageCheck<NotificationsDbContext>(
                name: "notifications-delivery-payload-partitions",
                schema: "notifications",
                table: "delivery_payload",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<NotificationsPartitionOptions>>()
                    .Value.FutureWindowMinimumDays);
        return services;
    }
}
