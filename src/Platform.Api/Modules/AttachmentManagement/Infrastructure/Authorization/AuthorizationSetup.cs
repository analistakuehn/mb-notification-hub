using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

/// <summary>Named policy for producers that manage attachment ingress.</summary>
public static class AuthorizationSetup
{
    public const string ProducerPolicyName = "attachments-producer";

    /// <summary>
    /// Named policy of the operations surface. It is disjoint from the
    /// producer grant on purpose: a producer is told one word for a whole
    /// family of refusals, and the reading that tells the checks apart belongs
    /// to whoever investigates, never to whoever would use it to find out
    /// which check to work around.
    /// </summary>
    public const string OperationsPolicyName = "attachments-operations";

    /// <summary>App role that grants the operations surface.</summary>
    public const string OperationsRole = "Notifications.Attachments.Operations";

    public static IServiceCollection AddAttachmentManagementAuthorization(
        this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(
                ProducerPolicyName,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new AttachmentProducerRequirement()))

            // Route gate only, the way the other reading surfaces of this
            // platform are gated: nothing binds a reading principal of this
            // role to an application, so there is no per-application scope to
            // enforce yet. What the route answers with carries no coordinate,
            // no name, no type and no proof of the bytes, so the reading is
            // bounded by what it can say and not only by who may ask.
            .AddPolicy(
                OperationsPolicyName,
                policy => policy.RequireRole(OperationsRole));
        services.AddScoped<IAttachmentProducerRegistry, AttachmentProducerRegistry>();
        services.AddScoped<IAuthorizationHandler, AttachmentProducerAuthorizationHandler>();
        return services;
    }

    internal static bool IsUnavailable(AuthorizationResult result)
        => result.Failure?.FailureReasons.Any(reason =>
            string.Equals(
                reason.Message,
                ErrorCodes.AuthorizationUnavailable,
                StringComparison.Ordinal)) == true;
}
