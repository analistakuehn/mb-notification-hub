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

    /// <summary>
    /// The automatic channel stop, composed only by the role that observes
    /// provider circuits. It is not part of the kill-switch composition above
    /// on purpose: every role evaluates the switch, and only the one that
    /// sends can see a circuit open. It carries the administration handler with
    /// it, because the automatic stop writes the very same transition an
    /// operator writes.
    /// </summary>
    internal static IServiceCollection AddAutomaticChannelKillSwitch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AutomaticChannelKillSwitchOptions>()
            .Bind(configuration.GetSection(AutomaticChannelKillSwitchOptions.SectionName))
            .Validate(
                options => options.OpenCircuitWindow > TimeSpan.Zero,
                $"A janela de '{AutomaticChannelKillSwitchOptions.SectionName}:OpenCircuitWindow' "
                + "precisa ser positiva; uma janela nula pararia o canal na primeira recusa do circuito.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ChannelCircuitObserver>();
        services.TryAddScoped<KillSwitchAdministration.Handler>();
        services.TryAddScoped<AutomaticChannelKillSwitch>();
        return services;
    }
}
