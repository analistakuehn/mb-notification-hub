using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Worker;

/// <summary>
/// Maps the configured worker role to the composition it hosts. The worker
/// host is a thin composition root: platform-owned roles are declared here,
/// module-owned roles arrive through discovery over the solution assemblies,
/// so this host never references a module namespace. Without a configured
/// role, or with an unknown one, the host refuses to boot: a worker running
/// without a function would look healthy while doing nothing.
/// </summary>
public static class WorkerRoleCatalog
{
    public const string RoleConfigurationKey = "Worker:Role";

    /// <summary>Producer-side relay: reads the platform outbox, publishes to SQS.</summary>
    public const string OutboxRelayRole = "outbox-relay";

    /// <summary>Core pipeline: consumes the core queues; composition owned by the Notifications module.</summary>
    public const string CoreRole = "core";

    /// <summary>Cache invalidation of contacts; composition owned by the ContactConsent module.</summary>
    public const string ContactConsentRole = "contact-consent";

    private static readonly Dictionary<string, Action<IServiceCollection, IConfiguration>> PlatformRoles =
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

        var roles = new Dictionary<string, Action<IServiceCollection, IConfiguration>>(
            PlatformRoles, StringComparer.Ordinal);
        foreach ((var moduleRole, Action<IServiceCollection, IConfiguration> configure) in
            ModuleRegistrationExtensions.DiscoverWorkerRoles(SolutionAssemblies.All))
        {
            if (!roles.TryAdd(moduleRole, configure))
            {
                throw new InvalidOperationException(
                    $"O papel de worker '{moduleRole}' colide com um papel da plataforma.");
            }
        }

        var role = configuration[RoleConfigurationKey];
        var knownRoles = string.Join(", ", roles.Keys.Order(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new InvalidOperationException(
                $"Nenhum papel configurado em '{RoleConfigurationKey}'; o host de worker não sobe sem função. "
                + $"Papéis conhecidos: {knownRoles}.");
        }

        if (!roles.TryGetValue(role, out Action<IServiceCollection, IConfiguration>? register))
        {
            throw new InvalidOperationException(
                $"Papel desconhecido '{role}' em '{RoleConfigurationKey}'. Papéis conhecidos: {knownRoles}.");
        }

        register(services, configuration);
        return services;
    }
}
