using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

namespace NotificationHub.Api.Modules.Dispatch;

public sealed class DispatchModule : IModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDispatchPersistence(configuration);
        services.AddDispatchProviders(configuration);
        services.AddDispatchProviderResolution(configuration);
        services.AddDispatchWebhookInterpreters(configuration);
        services.AddDispatchDeliveryLookups();
        services.TryAddSingleton(TimeProvider.System);
    }
}
