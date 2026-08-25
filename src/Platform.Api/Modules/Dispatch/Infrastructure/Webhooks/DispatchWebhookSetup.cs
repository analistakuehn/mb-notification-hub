using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

/// <summary>
/// Registers the provider-side half of delivery feedback: one interpreter per
/// provider plus the resolution that picks between them. The verification
/// secrets bind like every other provider credential in this module, which
/// means a host without them still boots and refuses callbacks at use time
/// instead of failing to start.
/// </summary>
public static class DispatchWebhookSetup
{
    public static IServiceCollection AddDispatchWebhookInterpreters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TwilioWebhookOptions>()
            .Bind(configuration.GetSection(TwilioWebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SendGridWebhookOptions>()
            .Bind(configuration.GetSection(SendGridWebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IProviderWebhookInterpreter, TwilioWebhookInterpreter>();
        services.AddSingleton<IProviderWebhookInterpreter, SendGridWebhookInterpreter>();
        services.AddSingleton<IProviderWebhookInterpreterResolver, ProviderWebhookInterpreterResolver>();

        return services;
    }
}
