using System.Security.Claims;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

internal sealed record AttachmentPrincipal(
    string Issuer,
    string ClaimKind,
    string PrincipalId)
{
    internal static AttachmentPrincipal? Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        Claim? identity = principal.FindFirst("oid")
            ?? principal.FindFirst("sub")
            ?? principal.FindFirst(ClaimTypes.NameIdentifier);
        return identity is null
            || string.IsNullOrWhiteSpace(identity.Value)
            || string.IsNullOrWhiteSpace(identity.Issuer)
            || string.Equals(
                identity.Issuer,
                ClaimsIdentity.DefaultIssuer,
                StringComparison.Ordinal)
            ? null
            : new AttachmentPrincipal(identity.Issuer, identity.Type, identity.Value);
    }
}
