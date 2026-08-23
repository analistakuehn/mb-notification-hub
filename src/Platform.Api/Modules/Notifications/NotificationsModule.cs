using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;

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

        services.AddScoped<PublishedTemplateGate>();
        services.AddScoped<VariablesProtector>();
        services.AddScoped<IngestionWriter>();
        services.AddScoped<RequestNotification.Handler>();
        services.TryAddScoped<IValidator<RequestNotification.Command>, RequestNotification.Validator>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder notifications = app.MapGroup("/v1/notifications");
        RequestNotification.MapEndpoint(notifications);
    }
}
