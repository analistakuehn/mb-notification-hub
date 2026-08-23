using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using Polly;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;

public static class DispatchProviderSetup
{
    public static IServiceCollection AddDispatchProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SendGridOptions>()
            .Bind(configuration.GetSection(SendGridOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<FcmOptions>()
            .Bind(configuration.GetSection(FcmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(SendGridChannelProvider.HttpClientName, (serviceProvider, client) =>
            {
                SendGridOptions config = serviceProvider
                    .GetRequiredService<IOptions<SendGridOptions>>().Value;
                client.BaseAddress = new Uri(config.BaseAddress);
            })
            .AddResilienceHandler("dispatch-sendgrid-pipeline", (builder, context) =>
            {
                SendGridOptions config = context.ServiceProvider
                    .GetRequiredService<IOptions<SendGridOptions>>().Value;
                AddSendPipeline(builder, config.CircuitBreaker, config.TimeoutSeconds);
            });

        services.AddHttpClient(FcmChannelProvider.HttpClientName, (serviceProvider, client) =>
            {
                FcmOptions config = serviceProvider
                    .GetRequiredService<IOptions<FcmOptions>>().Value;
                client.BaseAddress = new Uri(config.BaseAddress);
            })
            .AddResilienceHandler("dispatch-fcm-pipeline", (builder, context) =>
            {
                FcmOptions config = context.ServiceProvider
                    .GetRequiredService<IOptions<FcmOptions>>().Value;
                AddSendPipeline(builder, config.CircuitBreaker, config.TimeoutSeconds);
            });

        // Token acquisition is the only retried call in this module: the
        // grant is idempotent at the endpoint, unlike a message send.
        services.AddHttpClient(FcmAccessTokenSource.HttpClientName)
            .AddResilienceHandler("dispatch-fcm-token-pipeline", (builder, context) =>
            {
                FcmOptions config = context.ServiceProvider
                    .GetRequiredService<IOptions<FcmOptions>>().Value;
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(250),
                });
                builder.AddTimeout(TimeSpan.FromSeconds(config.TimeoutSeconds));
            });

        services.AddSingleton<FcmAccessTokenSource>();
        services.AddSingleton<SendGridChannelProvider>();
        services.AddSingleton<FcmChannelProvider>();
        services.AddSingleton<IChannelProvider>(serviceProvider =>
            new ConcurrencyLimitedChannelProvider(
                serviceProvider.GetRequiredService<SendGridChannelProvider>(),
                serviceProvider.GetRequiredService<IOptions<SendGridOptions>>().Value.MaxConcurrency));
        services.AddSingleton<IChannelProvider>(serviceProvider =>
            new ConcurrencyLimitedChannelProvider(
                serviceProvider.GetRequiredService<FcmChannelProvider>(),
                serviceProvider.GetRequiredService<IOptions<FcmOptions>>().Value.MaxConcurrency));

        return services;
    }

    // Circuit breaker outside, timeout inside, so every timed-out attempt
    // feeds the breaker. No retry on purpose: a provider send is not
    // idempotent, and redelivery is the queue's decision.
    private static void AddSendPipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        ProviderCircuitBreakerOptions circuitBreaker,
        int timeoutSeconds)
    {
        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = circuitBreaker.FailureRatio,
            SamplingDuration = TimeSpan.FromSeconds(circuitBreaker.SamplingDurationSeconds),
            MinimumThroughput = circuitBreaker.MinimumThroughput,
            BreakDuration = TimeSpan.FromSeconds(circuitBreaker.BreakDurationSeconds),
        });
        builder.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds));
    }
}
