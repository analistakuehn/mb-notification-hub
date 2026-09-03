using System.Security.Claims;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

/// <summary>One exact principal-to-application grant read by attachment ingress.</summary>
public sealed class ProducerApplicationGrant
{
    public const int MaxIssuerLength = 200;
    public const int MaxClaimKindLength = 100;
    public const int MaxPrincipalIdLength = 200;

    private ProducerApplicationGrant(
        string issuer,
        string claimKind,
        string principalId,
        string application)
    {
        Issuer = issuer;
        ClaimKind = claimKind;
        PrincipalId = principalId;
        Application = application;
    }

    // EF Core materialization: properties are populated from the store.
    private ProducerApplicationGrant()
    {
        Issuer = null!;
        ClaimKind = null!;
        PrincipalId = null!;
        Application = null!;
    }

    public string Issuer { get; }

    public string ClaimKind { get; }

    public string PrincipalId { get; }

    public string Application { get; }

    public static Result<ProducerApplicationGrant> Create(
        string? issuer,
        string? claimKind,
        string? principalId,
        string? application)
    {
        if (string.IsNullOrWhiteSpace(issuer)
            || issuer.Length > MaxIssuerLength
            || !IsSupportedClaimKind(claimKind)
            || string.IsNullOrWhiteSpace(principalId)
            || principalId.Length > MaxPrincipalIdLength
            || string.IsNullOrWhiteSpace(application)
            || application.Length > Attachment.MaxApplicationLength)
        {
            return Result.ValidationError<ProducerApplicationGrant>(
                ErrorCodes.InvalidProducerGrant);
        }

        return Result.Success(new ProducerApplicationGrant(
            issuer,
            claimKind!,
            principalId,
            application));
    }

    internal static bool IsSupportedClaimKind(string? claimKind)
        => claimKind is "oid" or "sub"
            || string.Equals(
                claimKind,
                ClaimTypes.NameIdentifier,
                StringComparison.Ordinal);
}
