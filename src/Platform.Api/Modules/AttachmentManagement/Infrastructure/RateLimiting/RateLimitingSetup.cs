using System.Net;
using System.Threading.RateLimiting;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;

/// <summary>
/// Per-principal abuse containment for attachment ingress. The generous limit
/// is a project baseline, not an operational traffic budget.
/// </summary>
public static class RateLimitingSetup
{
    public const string PolicyName = "attachment-management";

    private const int PermitLimit = 1000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddAttachmentManagementRateLimiting(
        this IServiceCollection services)
        => services.AddRateLimiter(options =>
            options.AddPolicy(PolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    })));

    internal static AttachmentRateLimitPartition PartitionKey(HttpContext httpContext)
    {
        var principal = AttachmentPrincipal.Resolve(httpContext.User);
        return principal is not null
            ? AttachmentRateLimitPartition.ForPrincipal(principal)
            : AttachmentRateLimitPartition.ForAddress(
                httpContext.Connection.RemoteIpAddress);
    }
}

internal readonly record struct AttachmentRateLimitPartition(
    string Category,
    string? Issuer,
    string? ClaimKind,
    string? PrincipalId,
    IPAddress? Address)
{
    internal static AttachmentRateLimitPartition ForPrincipal(
        AttachmentPrincipal principal)
        => new(
            "principal",
            principal.Issuer,
            principal.ClaimKind,
            principal.PrincipalId,
            null);

    internal static AttachmentRateLimitPartition ForAddress(IPAddress? address)
        => new("address", null, null, null, address);

    public override string ToString() => Category;
}
