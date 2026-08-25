using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using Polly;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;

/// <summary>
/// Registers the pull half of delivery feedback: one lookup per provider whose
/// platform can be asked after the send, plus the resolution that picks
/// between them. A provider without a lookup is absent from here on purpose,
/// and the resolver refuses for it.
/// <para>
/// It binds no options of its own. The lookups read the credentials of the
/// sending adapter and the failure vocabulary of the callback interpreter,
/// because a code that means a dead destination means the same thing whether
/// this hub was told or had to ask; composing the lookups therefore means
/// composing both other halves, and binding the same sections twice would only
/// duplicate the entries of every list they carry.
/// </para>
/// </summary>
public static class DispatchDeliveryLookupSetup
{
    public static IServiceCollection AddDispatchDeliveryLookups(this IServiceCollection services)
    {
        // Named clients of their own, and not the ones the sends use. The send
        // client carries a circuit breaker whose whole meaning is how the
        // provider answers sends: a batch read that times out is not a send
        // that failed, and counting it there would stop a channel over a
        // question nobody asked the provider to answer quickly.
        services.AddHttpClient(SendGridDeliveryLookup.HttpClientName, (serviceProvider, client) =>
            {
                SendGridOptions config = serviceProvider
                    .GetRequiredService<IOptions<SendGridOptions>>().Value;
                client.BaseAddress = new Uri(config.BaseAddress);
            })
            .AddResilienceHandler("dispatch-sendgrid-lookup-pipeline", (builder, context) =>
            {
                SendGridOptions config = context.ServiceProvider
                    .GetRequiredService<IOptions<SendGridOptions>>().Value;
                builder.AddTimeout(TimeSpan.FromSeconds(config.TimeoutSeconds));
            });

        services.AddHttpClient(TwilioDeliveryLookup.HttpClientName, (serviceProvider, client) =>
            {
                TwilioOptions config = serviceProvider
                    .GetRequiredService<IOptions<TwilioOptions>>().Value;
                client.BaseAddress = new Uri(config.BaseAddress);
            })
            .AddResilienceHandler("dispatch-twilio-lookup-pipeline", (builder, context) =>
            {
                TwilioOptions config = context.ServiceProvider
                    .GetRequiredService<IOptions<TwilioOptions>>().Value;
                builder.AddTimeout(TimeSpan.FromSeconds(config.TimeoutSeconds));
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IProviderDeliveryLookup, SendGridDeliveryLookup>();
        services.AddSingleton<IProviderDeliveryLookup, TwilioDeliveryLookup>();
        services.AddSingleton<IProviderDeliveryLookupResolver, ProviderDeliveryLookupResolver>();
        return services;
    }
}
