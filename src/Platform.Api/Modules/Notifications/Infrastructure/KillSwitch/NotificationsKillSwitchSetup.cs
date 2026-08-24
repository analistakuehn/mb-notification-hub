using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal static class NotificationsKillSwitchSetup
{
    internal const string HealthCheckName = "notifications-kill-switch";

    internal static IServiceCollection AddNotificationsKillSwitch(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IKillSwitchSnapshotSource, PostgresKillSwitchSnapshotSource>();
        services.TryAddSingleton<KillSwitchCache>();
        services.TryAddSingleton<IKillSwitch>(provider =>
            provider.GetRequiredService<KillSwitchCache>());
        services.TryAddScoped<KillSwitchHoldWriter>();
        services.TryAddScoped<KillSwitchHoldReleaser>();
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            HealthCheckName,
            provider => new KillSwitchHealthCheck(provider.GetRequiredService<KillSwitchCache>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "security"]));
        return services;
    }
}
