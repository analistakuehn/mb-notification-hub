using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Public composition surface of the platform SQS consuming: the shared
/// client, the consumer tuning, the transactional processed-messages store
/// and its purge job. Each consuming role then binds its own queues and
/// processor through <see cref="AddSqsQueueConsumer{TProcessor}"/>.
/// </summary>
public static class SqsConsumingSetup
{
    public static IServiceCollection AddSqsMessageConsuming(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OutboxSqsOptions>()
            .Bind(configuration.GetSection(OutboxSqsOptions.SectionName));
        services.AddOptions<SqsConsumerOptions>()
            .Bind(configuration.GetSection(SqsConsumerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ProcessedMessagePurgeOptions>()
            .Bind(configuration.GetSection(ProcessedMessagePurgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAmazonSQS>(serviceProvider => SqsClientFactory.Create(
            serviceProvider.GetRequiredService<IOptions<OutboxSqsOptions>>().Value));
        services.TryAddSingleton<SqsQueueUrlResolver>();

        // Stateless by design: every mark joins the caller's transaction.
        services.TryAddSingleton<IProcessedMessageStore, PostgresProcessedMessageStore>();
        services.AddScoped<ProcessedMessagePurge>();
        services.AddHostedService<ProcessedMessagePurgeService>();
        return services;
    }

    /// <summary>
    /// Hosts one consumer service for <typeparamref name="TProcessor"/> over
    /// the given queues. The processor and the poison sink stay scoped
    /// registrations owned by the consuming role's module.
    /// </summary>
    public static IServiceCollection AddSqsQueueConsumer<TProcessor>(
        this IServiceCollection services,
        IReadOnlyList<SqsQueueBinding> queues)
        where TProcessor : class, ISqsMessageProcessor
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentOutOfRangeException.ThrowIfZero(queues.Count);

        services.AddScoped<TProcessor>();
        services.AddSingleton(new SqsConsumerPlan<TProcessor>(queues));
        services.AddHostedService<SqsConsumerService<TProcessor>>();
        return services;
    }
}
