namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

/// <summary>
/// Named authorization policies of the template-management surface. Authoring
/// and catalog reads require the author role carried by the bearer token; the
/// publisher policy arrives with the publish workflow.
/// </summary>
public static class AuthorizationSetup
{
    public const string AuthorPolicyName = "templates-author";
    public const string AuthorRole = "Templates.Author";

    public static IServiceCollection AddTemplateManagementAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorPolicyName, policy => policy.RequireRole(AuthorRole));
        return services;
    }
}
