using System.Security.Claims;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

/// <summary>
/// Resolves the stable identity of the authenticated principal. Prefers the
/// identity provider object id (<c>oid</c>), then <c>sub</c>, then the mapped
/// name identifier claim. Authorship and editor tracking depend on this value.
/// </summary>
internal static class CurrentActor
{
    internal static Result<string> Identify(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var actorId = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrWhiteSpace(actorId)
            ? Result.Forbidden<string>(DomainError.Format(
                ErrorCodes.ActorUnidentified,
                "The access token carries no stable subject identity (oid or sub claim)."))
            : Result.Success(actorId);
    }
}
