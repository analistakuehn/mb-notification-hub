using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;
using NotificationHub.Api.Modules.Notifications.Features.History;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications;

public sealed class NotificationsModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddNotificationsPersistence(configuration);
        services.AddNotificationsPartitioning(configuration);
        services.AddNotificationsAuthorization();
        services.AddNotificationsProviderSignature(configuration);
        services.AddNotificationsKillSwitch();
        services.AddNotificationsRateLimiting();
        services.TryAddSingleton(TimeProvider.System);

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

        services.AddOptions<IdempotencyPurgeOptions>()
            .Bind(configuration.GetSection(IdempotencyPurgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IdempotencyPurge>();
        services.AddHostedService<IdempotencyPurgeService>();

        services.AddSingleton<IngressControls>();
        services.AddSingleton<RequestNotification.IIngressAdmission, RequestNotification.IngressAdmission>();

        services.AddScoped<PublishedTemplateGate>();
        services.AddScoped<VariablesProtector>();
        services.AddScoped<IngestionWriter>();
        // The synchronous posture: a rejection trail commits as soon as the
        // outcome is known, because the caller is about to receive the answer.
        services.AddScoped<IIngestionSink, CommittedIngestionSink>();
        services.AddScoped<RequestNotification.Handler>(static provider => new RequestNotification.Handler(
            provider.GetRequiredService<IValidator<RequestNotification.Command>>(),
            provider.GetRequiredService<PublishedTemplateGate>(),
            provider.GetRequiredService<RequestNotification.IIngressAdmission>(),
            provider.GetRequiredService<VariablesProtector>(),
            provider.GetRequiredService<IIngestionSink>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<RequestNotification.Handler>>()));
        services.AddScoped<KillSwitchAdministration.Handler>();
        services.TryAddScoped<IValidator<RequestNotification.Command>, RequestNotification.Validator>();

        // Query surface: read-only context, target enrichment through the
        // published contact contract, and the access log that records who read
        // what without appending to the audit trail.
        services.AddSingleton<NotificationQueryAccessLog>();
        services.AddScoped<AttemptTargetDirectory>();
        services.AddScoped<NotificationHistoryReader>();
        services.AddScoped<GetNotification.Handler>();
        services.AddScoped<ListRecipientNotifications.Handler>();
        services.AddScoped<ListNotificationsByCorrelation.Handler>();

        // Reconstruction surface: the projected policy evidence and the stored
        // render, opened inside this module and never handed over encrypted.
        services.AddScoped<INotificationEvidence, NotificationEvidenceReader>();

        // Delivery-feedback ingestion: the transactional write of the evidence
        // and the use case behind the provider webhook route. The application
        // of what the feedback means lives in the delivery-tracker role, never
        // in this host.
        services.AddOptions<ProviderWebhookIngestionOptions>()
            .Bind(configuration.GetSection(ProviderWebhookIngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<DeliveryEventWriter>();
        services.AddScoped<ReceiveProviderWebhook.Handler>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder notifications = app.MapGroup("/v1/notifications");
        RequestNotification.MapEndpoint(notifications);
        GetNotification.MapEndpoint(notifications);
        ListNotificationsByCorrelation.MapEndpoint(notifications);

        RouteGroupBuilder recipients = app.MapGroup("/v1/recipients");
        ListRecipientNotifications.MapEndpoint(recipients);

        RouteGroupBuilder killSwitch = notifications.MapGroup("/kill-switch");
        KillSwitchAdministration.MapEndpoint(killSwitch);

        // Outside the versioned surface on purpose: the address is given to a
        // provider and quoted in its console, so it never carries a version
        // this hub would have to keep answering forever.
        RouteGroupBuilder webhooks = app.MapGroup("/webhooks");
        ReceiveProviderWebhook.MapEndpoint(webhooks);
    }
}
