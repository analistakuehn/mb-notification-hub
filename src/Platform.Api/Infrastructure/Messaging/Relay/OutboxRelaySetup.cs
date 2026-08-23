using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Public composition surface of the Outbox Relay, the producer side of the
/// platform messaging: the worker host calls this after
/// <see cref="PlatformMessagingSetup.AddPlatformMessaging"/> to host the
/// relay loop, the SQS publisher and the relay health check. Consuming
/// belongs to the consumer slices, never here.
/// </summary>
public static class OutboxRelaySetup
{
    public static IServiceCollection AddOutboxRelay(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OutboxRelayOptions>()
            .Bind(configuration.GetSection(OutboxRelayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Bands.All(name => OutboxBands.TryParseName(name, out _)),
                "Quando configuradas, as bandas do relay devem pertencer a: auth, critical, transactional, operational.")
            .ValidateOnStart();
        services.AddOptions<OutboxSqsOptions>()
            .Bind(configuration.GetSection(OutboxSqsOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAmazonSQS>(serviceProvider => SqsClientFactory.Create(
            serviceProvider.GetRequiredService<IOptions<OutboxSqsOptions>>().Value));
        services.TryAddSingleton<SqsQueueUrlResolver>();
        services.AddSingleton<OutboxRelayHealthState>();
        services.AddSingleton<IOutboxPublisher, SqsOutboxPublisher>();
        services.AddScoped<IOutboxPendingStore, PostgresOutboxPendingStore>();
        services.AddScoped<OutboxRelay>();
        services.AddHostedService<OutboxRelayService>();
        services.AddHealthChecks()
            .AddCheck<OutboxRelayHealthCheck>("outbox-relay");
        return services;
    }
}
