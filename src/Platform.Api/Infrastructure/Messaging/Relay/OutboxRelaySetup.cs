using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Public composition surface of the Outbox Relay, the producer side of the
/// platform messaging: the worker host calls this after
/// <see cref="PlatformMessagingSetup.AddPlatformMessaging"/> to host the
/// relay loop, the transport publishers and the relay health check. Consuming
/// belongs to the consumer slices, never here.
///
/// Publishers are keyed by transport: the relay resolves one per lane, so a
/// destination name never decides which client ships a row. The Kafka lane
/// only exists when the cluster is configured; without it, bus rows stay
/// pending and visible on the per-transport backlog instead of failing a
/// deployment that has no bus.
/// </summary>
public static class OutboxRelaySetup
{
    public static IServiceCollection AddOutboxRelay(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        OutboxKafkaOptions? kafka = configuration
            .GetSection(OutboxKafkaOptions.SectionName)
            .Get<OutboxKafkaOptions>();
        var kafkaConfigured = kafka?.BootstrapServers is { Length: > 0 };

        services.AddOptions<OutboxRelayOptions>()
            .Bind(configuration.GetSection(OutboxRelayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Bands.All(name => OutboxBands.TryParseName(name, out _)),
                "Quando configuradas, as bandas do relay devem pertencer a: auth, critical, transactional, operational.")
            .Validate(
                options => options.Transports.All(OutboxTransports.IsKnown),
                "Quando configurados, os transportes do relay devem pertencer a: sqs, kafka.")
            .Validate(
                options => kafkaConfigured
                    || !options.Transports.Contains(OutboxTransports.Kafka, StringComparer.Ordinal),
                "O transporte 'kafka' foi exigido no relay sem servidores de bootstrap configurados.")
            .ValidateOnStart();
        services.AddOptions<OutboxSqsOptions>()
            .Bind(configuration.GetSection(OutboxSqsOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAmazonSQS>(serviceProvider => SqsClientFactory.Create(
            serviceProvider.GetRequiredService<IOptions<OutboxSqsOptions>>().Value));
        services.TryAddSingleton<SqsQueueUrlResolver>();
        services.AddSingleton<OutboxRelayHealthState>();

        var transports = new List<string> { OutboxTransports.Sqs };
        services.AddKeyedSingleton<IOutboxPublisher, SqsOutboxPublisher>(OutboxTransports.Sqs);
        if (kafkaConfigured)
        {
            services.AddOptions<OutboxKafkaOptions>()
                .Bind(configuration.GetSection(OutboxKafkaOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.AddKeyedSingleton<IOutboxPublisher, KafkaOutboxPublisher>(OutboxTransports.Kafka);
            transports.Add(OutboxTransports.Kafka);
        }

        services.AddSingleton(new OutboxPublisherRegistrations(transports));
        services.AddSingleton<IOutboxPublisherRegistry, KeyedOutboxPublisherRegistry>();
        services.AddScoped<IOutboxPendingStore, PostgresOutboxPendingStore>();
        services.AddScoped<OutboxRelay>();
        services.AddHostedService<OutboxRelayService>();
        services.AddHealthChecks()
            .AddCheck<OutboxRelayHealthCheck>("outbox-relay");
        return services;
    }
}
