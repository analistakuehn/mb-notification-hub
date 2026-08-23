namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;

/// <summary>
/// Named authorization policy of the audit surface. The role belongs to
/// Compliance and Internal Audit and it is disjoint from every other role of the
/// platform: producing, publishing and supporting are different jobs, and none
/// of them grants the right to read rendered content or contact data.
/// </summary>
public static class ComplianceAuthorizationSetup
{
    public const string AuditPolicyName = "compliance-audit";

    /// <summary>App role that grants the audit surface.</summary>
    public const string AuditRole = "Notifications.Audit";

    public static IServiceCollection AddComplianceAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()

            // Route gate only, exactly like the support query surface: this
            // phase has nothing that binds a reading principal to an
            // application, so no per-application scope exists to enforce. The
            // containment is that every route answers for an exact identity and
            // that every answer records its own disclosure.
            //
            // The role check goes through a requirement of its own rather than
            // through RequireRole, because a refused access to this surface must
            // leave a security log, and a bare role check has nowhere to put it.
            .AddPolicy(AuditPolicyName, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new AuditAccessRequirement()));
        return services;
    }
}
