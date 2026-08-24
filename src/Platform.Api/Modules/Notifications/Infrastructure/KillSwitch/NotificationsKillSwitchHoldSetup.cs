using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal static class NotificationsKillSwitchHoldSetup
{
    internal static IServiceCollection AddNotificationsKillSwitchHolds(
        this IServiceCollection services)
    {
        services.TryAddScoped<ApplicationKillSwitchGate>();
        services.TryAddScoped<ChannelKillSwitchGate>();
        services.TryAddScoped<KillSwitchHoldWriter>();
        services.TryAddScoped<KillSwitchHoldReleaser>();
        return services;
    }

    internal static IServiceCollection AddNotificationsKillSwitchHoldReleaser(
        this IServiceCollection services)
    {
        services.AddNotificationsKillSwitchHolds();
        services.AddHostedService<KillSwitchHoldReleaseService>();
        return services;
    }
}
