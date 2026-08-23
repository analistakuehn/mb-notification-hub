using System.Security.Claims;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;

/// <summary>
/// Resolves the stable identity of the principal writing contact data, for
/// the consent ledger's actor column and the audit trail. Prefers the
/// application id (<c>appid</c>, a client-credentials writer such as the
/// registration system), then the object id (<c>oid</c>), then <c>sub</c>. A
/// principal identified by <c>appid</c> audits as a system actor; a human
/// identity audits as a user.
/// </summary>
internal static class ContactWriterIdentity
{
    internal static (string ActorId, string ActorType)? Identify(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var applicationId = principal.FindFirstValue("appid");
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            return (applicationId, AuditActorTypes.System);
        }

        var userId = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(userId) ? null : (userId, AuditActorTypes.User);
    }
}
