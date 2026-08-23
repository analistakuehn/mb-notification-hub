using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

public static class DispatchProviderConfigSetup
{
    public static IServiceCollection AddDispatchProviderResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ProviderConfigOptions>()
            .Bind(configuration.GetSection(ProviderConfigOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IProviderConfigStore, CachedProviderConfigStore>();
        services.AddSingleton<IChannelProviderResolver, ChannelProviderResolver>();

        return services;
    }
}
