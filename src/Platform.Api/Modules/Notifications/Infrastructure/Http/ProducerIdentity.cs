using System.Security.Claims;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// Resolves the stable identity of the producer principal for the
/// <c>requested_by</c> column and the audit trail. Prefers the application id
/// (<c>appid</c>, the identity of a client-credentials producer), then the
/// object id (<c>oid</c>), then <c>sub</c>, then the mapped name identifier.
/// </summary>
internal static class ProducerIdentity
{
    internal static string? Identify(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identity = principal.FindFirstValue("appid")
            ?? principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(identity) ? null : identity;
    }
}
