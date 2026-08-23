using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Worker;

/// <summary>
/// Maps the configured worker role to the platform composition it hosts. The
/// worker host is a thin composition root: every hosted service belongs to
/// its owner (platform infrastructure today, modules later) and joins here
/// keyed by role, never unconditionally. Without a configured role, or with
/// an unknown one, the host refuses to boot: a worker running without a
/// function would look healthy while doing nothing.
/// </summary>
public static class WorkerRoleCatalog
{
    public const string RoleConfigurationKey = "Worker:Role";

    /// <summary>Producer-side relay: reads the platform outbox, publishes to SQS.</summary>
    public const string OutboxRelayRole = "outbox-relay";

    private static readonly Dictionary<string, Action<IServiceCollection, IConfiguration>> Roles =
        new(StringComparer.Ordinal)
        {
            [OutboxRelayRole] = static (services, configuration) =>
            {
                services.AddPlatformMessaging(configuration);
                services.AddOutboxRelay(configuration);
            },
        };

    public static IServiceCollection Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var role = configuration[RoleConfigurationKey];
        var knownRoles = string.Join(", ", Roles.Keys.Order(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new InvalidOperationException(
                $"Nenhum papel configurado em '{RoleConfigurationKey}'; o host de worker não sobe sem função. "
                + $"Papéis conhecidos: {knownRoles}.");
        }

        if (!Roles.TryGetValue(role, out Action<IServiceCollection, IConfiguration>? register))
        {
            throw new InvalidOperationException(
                $"Papel desconhecido '{role}' em '{RoleConfigurationKey}'. Papéis conhecidos: {knownRoles}.");
        }

        register(services, configuration);
        return services;
    }
}
