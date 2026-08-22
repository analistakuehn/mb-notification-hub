namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

/// <summary>
/// Named authorization policies of the template-management surface. Authoring
/// and catalog reads require the author role carried by the bearer token;
/// lifecycle transitions (publish, deprecate, disable, rollback) require the
/// publisher role. Role checks stop at the route; the four-eyes rule is
/// evaluated against the resource by the domain, not here.
/// </summary>
public static class AuthorizationSetup
{
    public const string AuthorPolicyName = "templates-author";
    public const string AuthorRole = "Templates.Author";
    public const string PublisherPolicyName = "templates-publisher";
    public const string PublisherRole = "Templates.Publish";

    public static IServiceCollection AddTemplateManagementAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorPolicyName, policy => policy.RequireRole(AuthorRole))
            .AddPolicy(PublisherPolicyName, policy => policy.RequireRole(PublisherRole));
        return services;
    }
}
