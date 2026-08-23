using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// Named authorization policy of the ingestion surface: the route only admits
/// principals carrying at least one send role. The class-level check runs
/// against the resource, in the use case, because the requested class arrives
/// in the body: a principal with a send role for one class still receives 403
/// when it asks for a class its token does not cover.
/// </summary>
public static class NotificationsAuthorizationSetup
{
    public const string SendPolicyName = "notifications-send";

    public static IServiceCollection AddNotificationsAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(SendPolicyName, policy => policy.RequireRole(
                NotificationClasses.RequiredRole(NotificationClasses.Critical),
                NotificationClasses.RequiredRole(NotificationClasses.Transactional),
                NotificationClasses.RequiredRole(NotificationClasses.Operational)));
        return services;
    }
}
