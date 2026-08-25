using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Composition of the <c>kafka-ingress</c> worker role, owned by this module:
/// the bus consumer of producer requests, the producer registry that answers
/// authorization on that transport, and exactly the ingestion collaborators
/// the shared use case needs. Nothing of the REST surface is hosted here, and
/// no ingestion rule is re-registered: the role hosts the same handler the
/// route calls.
/// </summary>
public sealed class KafkaIngressWorkerRole : IWorkerRoleModule
{
    public static string Role => "kafka-ingress";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPlatformMessaging(configuration);
        services.AddKafkaMessageConsuming(configuration);
        services.AddEnvelopeEncryption(configuration);
        services.AddAuditTrailSurface();
        services.AddTemplateManagementReadSurface(configuration);
        services.AddNotificationsPersistence(configuration);
        services.AddNotificationsKillSwitch();

        services.AddOptions<NotificationsRedisOptions>()
            .Bind(configuration.GetSection(NotificationsRedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<NotificationsRedisConnection>();

        services.AddOptions<IngestionRateLimitOptions>()
            .Bind(configuration.GetSection(IngestionRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IngestionRateLimiter>();
        services.AddSingleton<IdempotencyFastPath>();
        services.AddSingleton<IngressControls>();

        services.AddOptions<ProducerRegistryOptions>()
            .Bind(configuration.GetSection(ProducerRegistryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IProducerRegistry, CachedProducerRegistry>();
        services.AddScoped<KafkaProducerAuthorizer>();

        // Replaces the always-open default: this role decides nothing without
        // the registry, so it must not subscribe without it.
        services.AddSingleton<IKafkaConsumerGate, ProducerRegistryConsumerGate>();

        services.AddOptions<KafkaIngressOptions>()
            .Bind(configuration.GetSection(KafkaIngressOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KafkaIngressOptions>, KafkaIngressOptionsValidator>();
        services.AddSingleton<IngressDeadLetterWriter>();

        services.AddScoped<PublishedTemplateGate>();
        services.AddScoped<VariablesProtector>();
        services.AddScoped<IngestionWriter>();
        services.AddScoped<IngressCommitWriter>();
        services.AddScoped<KafkaIngressSettlement>();

        // The asynchronous posture: the rejection trail waits for the
        // dead-letter record to exist and commits with the deduplication mark.
        services.AddScoped<DeferredTrailIngestionSink>();
        services.AddScoped<IIngestionSink>(serviceProvider =>
            serviceProvider.GetRequiredService<DeferredTrailIngestionSink>());

        services.AddScoped<RequestNotification.Handler>();
        services.TryAddScoped<IValidator<RequestNotification.Command>, RequestNotification.Validator>();

        KafkaIngressOptions ingress = configuration
            .GetSection(KafkaIngressOptions.SectionName)
            .Get<KafkaIngressOptions>() ?? new KafkaIngressOptions();
        var topicMap = KafkaIngressTopicMap.Create(ingress);
        services.AddSingleton(topicMap);
        services.AddKafkaTopicConsumer<KafkaIngressProcessor>(
            topicMap.ConsumerGroup, topicMap.Topics);
    }
}
