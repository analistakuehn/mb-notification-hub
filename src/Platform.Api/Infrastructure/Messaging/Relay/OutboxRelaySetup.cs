using Amazon;
using Amazon.Runtime;
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
        services.TryAddSingleton<IAmazonSQS>(serviceProvider => CreateSqsClient(
            serviceProvider.GetRequiredService<IOptions<OutboxSqsOptions>>().Value));
        services.AddSingleton<SqsQueueUrlResolver>();
        services.AddSingleton<OutboxRelayHealthState>();
        services.AddSingleton<IOutboxPublisher, SqsOutboxPublisher>();
        services.AddScoped<IOutboxPendingStore, PostgresOutboxPendingStore>();
        services.AddScoped<OutboxRelay>();
        services.AddHostedService<OutboxRelayService>();
        services.AddHealthChecks()
            .AddCheck<OutboxRelayHealthCheck>("outbox-relay");
        return services;
    }

    private static AmazonSQSClient CreateSqsClient(OutboxSqsOptions options)
    {
        var config = new AmazonSQSConfig();
        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl;
            if (options.Region is not null)
            {
                config.AuthenticationRegion = options.Region;
            }
        }
        else if (options.Region is not null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        // Without static keys the SDK falls back to its default credential
        // chain (instance profile, environment), which is the production path.
        return options is { AccessKey: not null, SecretKey: not null }
            ? new AmazonSQSClient(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonSQSClient(config);
    }
}
