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

        // Registered beside the resolution and never apart from it: the
        // question it answers is answered by the adapter the resolution
        // returns, so a role composed with one and not the other would have a
        // planner reading a different deployment than the one that sends.
        services.AddSingleton<IChannelAttachmentSupport, ChannelAttachmentSupport>();

        return services;
    }
}
