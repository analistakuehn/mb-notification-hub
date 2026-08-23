using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Public composition surface of the platform bus consuming: the consumer
/// tuning, the dead-letter producer and the transactional processed-messages
/// store. Each consuming role then binds its own topics and processor through
/// <see cref="AddKafkaTopicConsumer{TProcessor}"/>.
/// </summary>
public static class KafkaConsumingSetup
{
    public static IServiceCollection AddKafkaMessageConsuming(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<KafkaConsumerOptions>()
            .Bind(configuration.GetSection(KafkaConsumerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.BootstrapServers.Length > 0,
                "O consumo do barramento exige servidores de bootstrap configurados.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Stateless by design: every mark joins the caller's transaction.
        services.TryAddSingleton<IProcessedMessageStore, PostgresProcessedMessageStore>();
        services.TryAddSingleton<IKafkaDeadLetterProducer, KafkaDeadLetterProducer>();
        services.TryAddSingleton<IKafkaConsumerGate, AlwaysOpenKafkaConsumerGate>();
        services.AddScoped<ProcessedMessagePurge>();
        services.AddHostedService<ProcessedMessagePurgeService>();
        return services;
    }

    /// <summary>
    /// Hosts one consumer for <typeparamref name="TProcessor"/> over the given
    /// topics, under the consumer group that identifies the role. The
    /// processor stays a scoped registration owned by the role's module.
    /// </summary>
    public static IServiceCollection AddKafkaTopicConsumer<TProcessor>(
        this IServiceCollection services,
        string groupId,
        IReadOnlyList<string> topics)
        where TProcessor : class, IKafkaMessageProcessor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentOutOfRangeException.ThrowIfZero(topics.Count);

        services.AddScoped<TProcessor>();
        services.AddSingleton(new KafkaConsumerPlan<TProcessor>(groupId, topics));
        services.AddHostedService<KafkaConsumerService<TProcessor>>();
        services.AddHealthChecks()
            .AddCheck<KafkaConsumerGateHealthCheck>("kafka-consumer-gate");
        return services;
    }
}

/// <summary>
/// Reports the role unhealthy while its subscription precondition does not
/// hold. Unhealthy rather than degraded on purpose: the role is not consuming
/// at all, and a deployment that looked healthy while reading nothing is the
/// exact failure the gate exists to prevent.
/// </summary>
internal sealed class KafkaConsumerGateHealthCheck(IKafkaConsumerGate gate) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        KafkaGateDecision decision = await gate.EvaluateAsync(cancellationToken);
        return decision.CanConsume
            ? HealthCheckResult.Healthy("Consumo do barramento habilitado.")
            : HealthCheckResult.Unhealthy(
                $"O papel não assina o tópico: {decision.Reason}.");
    }
}
