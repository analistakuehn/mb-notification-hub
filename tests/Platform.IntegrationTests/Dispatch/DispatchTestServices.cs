using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// Builds the module's real service graph (same registrations the host
/// uses) against test configuration, so contract tests exercise the actual
/// HTTP clients, resilience pipelines and cache wiring.
/// </summary>
internal static class DispatchTestServices
{
    public static ServiceProvider BuildProviderHost(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDispatchProviders(configuration);
        return services.BuildServiceProvider();
    }

    public static ServiceProvider BuildResolutionHost(
        string connectionString,
        TimeProvider timeProvider,
        params IChannelProvider[] hostedProviders)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Persistence:Ef:ConnectionString"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddDispatchPersistence(configuration);
        services.AddDispatchProviderResolution(configuration);
        foreach (IChannelProvider provider in hostedProviders) services.AddSingleton(provider);

        return services.BuildServiceProvider();
    }

    public static ServiceProvider BuildWebhookHost(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDispatchWebhookInterpreters(configuration);
        return services.BuildServiceProvider();
    }

    public static IChannelProvider ResolveProviderByKey(ServiceProvider services, string providerKey)
        => services.GetServices<IChannelProvider>()
            .Single(provider => provider.ProviderKey == providerKey);
}
