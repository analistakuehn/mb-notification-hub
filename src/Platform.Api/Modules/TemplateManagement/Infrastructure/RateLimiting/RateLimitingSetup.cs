using System.Threading.RateLimiting;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;

/// <summary>
/// Named rate-limit policy attached to every TemplateManagement endpoint.
/// Authoring is a low-volume administrative surface; the window is generous and
/// exists to stop scripted abuse, not to shape normal traffic. The budget is
/// partitioned per authenticated principal so one client can never exhaust
/// another client's window; the middleware runs after authentication, which is
/// what makes the principal available here.
/// </summary>
public static class RateLimitingSetup
{
    public const string PolicyName = "template-management";

    private const int PermitLimit = 1000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddTemplateManagementRateLimiting(this IServiceCollection services)
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

    /// <summary>
    /// One budget per authenticated principal, resolved through the same
    /// identity chain the audit trail uses (oid, then sub, then the mapped
    /// name identifier). Anonymous requests fall back to a per-address
    /// partition and never consume an authenticated budget.
    /// </summary>
    private static string PartitionKey(HttpContext httpContext)
    {
        Result<string> actor = CurrentActor.Identify(httpContext.User);
        return actor.IsSuccess
            ? $"principal:{actor.Value}"
            : $"address:{httpContext.Connection.RemoteIpAddress}";
    }
}
