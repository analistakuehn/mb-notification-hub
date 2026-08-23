using Microsoft.AspNetCore.Authorization;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;

/// <summary>
/// The audit role, expressed as a requirement rather than as a bare role check,
/// so a denial has a place to be observed. A refused access records a security
/// log and never an <c>audit_event</c>: the trail is a record of disclosure, and
/// an access that disclosed nothing does not belong in the hash chain. Keeping
/// it out also keeps the chain from being fattened by cheap probing.
/// </summary>
public sealed class AuditAccessRequirement : IAuthorizationRequirement
{
    public const string RoleClaimType = "role";
}
