using System.Security.Claims;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// The identity this surface records for a disclosure, and the route it
/// happened on. The object id comes first because the audit role belongs to
/// people: an application id only appears when a tool holds the role, and the
/// value recorded is whatever the token actually carries, never a value this
/// code invents.
/// </summary>
internal static class AuditPrincipal
{
    private const string Unknown = "(desconhecido)";

    internal static string Of(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("appid")
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Unknown;
    }

    internal static string RouteOf(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.GetEndpoint()?.DisplayName
            ?? httpContext.Request.Path.Value
            ?? "(desconhecida)";
    }
}
