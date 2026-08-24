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

    /// <summary>
    /// Named policy of the query surface. The role belongs to support and to
    /// internal tooling, never to a producer: producing and reading are
    /// different jobs, and the send roles carry no read grant.
    /// </summary>
    public const string ReadPolicyName = "notifications-read";

    public const string KillSwitchAdminPolicyName = "notifications-kill-switch-admin";

    public const string KillSwitchAdminRole = "Platform.Admin";

    /// <summary>App role that grants the query surface.</summary>
    public const string ReadRole = "Notifications.Read";

    public static IServiceCollection AddNotificationsAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(SendPolicyName, policy => policy.RequireRole(
                NotificationClasses.RequiredRole(NotificationClasses.Critical),
                NotificationClasses.RequiredRole(NotificationClasses.Transactional),
                NotificationClasses.RequiredRole(NotificationClasses.Operational)))
            // Route gate only: this phase has no per-application scope for the
            // read, because nothing binds a reading principal to an
            // application. The containment lives in the routes themselves,
            // which only ever answer for an exact identity.
            .AddPolicy(ReadPolicyName, policy => policy.RequireRole(ReadRole))
            .AddPolicy(
                KillSwitchAdminPolicyName,
                policy => policy.RequireRole(KillSwitchAdminRole));
        return services;
    }
}
