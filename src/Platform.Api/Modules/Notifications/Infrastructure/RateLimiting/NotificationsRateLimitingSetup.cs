using System.Security.Claims;
using System.Threading.RateLimiting;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>
/// Named in-process rate-limit policy attached to the ingestion endpoint as a
/// coarse backstop against scripted abuse by one principal. The business
/// limits of the ingestion (per principal and class, per recipient) live in
/// the Redis-backed <see cref="IngestionRateLimiter"/>, which owns the 429
/// contract with Retry-After; this policy only caps the raw request rate
/// before any work happens.
/// </summary>
public static class NotificationsRateLimitingSetup
{
    public const string PolicyName = "notifications-ingestion";

    /// <summary>
    /// Named policy of the query surface, separate from the ingestion one on
    /// purpose: the ingestion ceiling is sized for a service producing traffic,
    /// and reusing it here would hand a human or a script a producer-sized
    /// budget for sweeping the store.
    /// </summary>
    public const string QueryPolicyName = "notifications-query";

    private const int PermitLimit = 2000;
    private const int QueryPermitLimit = 120;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddNotificationsRateLimiting(this IServiceCollection services)
        => services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(QueryPolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = QueryPermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));
        });

    private static string PartitionKey(HttpContext httpContext)
    {
        var principal = httpContext.User.FindFirstValue("appid")
            ?? httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("sub");
        return principal is null
            ? $"address:{httpContext.Connection.RemoteIpAddress}"
            : $"principal:{principal}";
    }
}
