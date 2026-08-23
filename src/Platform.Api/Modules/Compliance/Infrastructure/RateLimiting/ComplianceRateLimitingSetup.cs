using System.Security.Claims;
using System.Threading.RateLimiting;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.RateLimiting;

/// <summary>
/// Named in-process rate-limit policies of the audit surface, partitioned by
/// principal. The two routes get separate budgets on purpose: reconstructing one
/// notification is the normal work of an auditor, while opening the stored
/// content of an attempt is the single most sensitive read in the platform, and
/// a shared budget would let a sweep of content hide inside a normal volume of
/// evidence reads.
/// </summary>
public static class ComplianceRateLimitingSetup
{
    public const string EvidencePolicyName = "compliance-audit-evidence";

    /// <summary>Budget of its own for the route that opens stored content.</summary>
    public const string ContentPolicyName = "compliance-audit-content";

    private const int EvidencePermitLimit = 120;
    private const int ContentPermitLimit = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddComplianceRateLimiting(this IServiceCollection services)
        => services.AddRateLimiter(options =>
        {
            options.AddPolicy(EvidencePolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = EvidencePermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(ContentPolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = ContentPermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));
        });

    private static string PartitionKey(HttpContext httpContext)
    {
        var principal = httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("appid")
            ?? httpContext.User.FindFirstValue("sub");
        return principal is null
            ? $"address:{httpContext.Connection.RemoteIpAddress}"
            : $"principal:{principal}";
    }
}
