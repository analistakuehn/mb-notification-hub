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
                    .Value.FutureWindowMinimumDays);
        return services;
    }
}
