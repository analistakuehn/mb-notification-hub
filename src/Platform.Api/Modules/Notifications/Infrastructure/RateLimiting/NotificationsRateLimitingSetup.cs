using System.Security.Claims;
using System.Threading.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

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

    /// <summary>
    /// Restrictive policy for administrative kill-switch transitions, isolated
    /// from producer and query traffic and partitioned by the acting principal.
    /// </summary>
    public const string KillSwitchAdminPolicyName = "notifications-kill-switch-admin";

    /// <summary>
    /// Policy of the provider webhook route, partitioned by the provider the
    /// callback is addressed to rather than by the caller: the callers are the
    /// provider's own hosts, so an address partition would either be one
    /// bucket for a whole provider fleet or a new bucket per node. Partitioning
    /// by provider also contains the blast radius, since one provider's event
    /// storm cannot starve another provider's feedback.
    /// </summary>
    public const string ProviderWebhookPolicyName = "notifications-provider-webhook";

    private const int PermitLimit = 2000;
    private const int QueryPermitLimit = 120;
    private const int KillSwitchAdminPermitLimit = 30;

    /// <summary>
    /// Deliberately generous: a provider decides its own callback rate, a
    /// batch of feedback is cheap to store, and a refused callback is
    /// redelivered by the provider until it is accepted. The ceiling is a
    /// backstop against a runaway loop, not a business limit.
    /// </summary>
    private const int ProviderWebhookPermitLimit = 6000;

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

            options.AddPolicy(KillSwitchAdminPolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ActorPartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = KillSwitchAdminPermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(ProviderWebhookPolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ProviderPartitionKey(httpContext),
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = ProviderWebhookPermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));
        });

    /// <summary>
    /// Partition of the webhook route: the proven provider identity when the
    /// signature scheme published one, and the addressed provider otherwise,
    /// so a flood of unauthenticated callbacks still lands in a bucket instead
    /// of sharing one with authenticated traffic.
    /// </summary>
    private static string ProviderPartitionKey(HttpContext httpContext)
    {
        var proven = httpContext.User.FindFirstValue(ProviderSignatureDefaults.ProviderKeyClaimType);
        if (proven is not null) return $"provider:{proven}";

        return httpContext.Request.RouteValues
                   .TryGetValue(ProviderSignatureDefaults.ProviderRouteValue, out var addressed)
               && addressed is string providerKey
                ? $"unverified:{providerKey}"
                : $"unverified:{httpContext.Connection.RemoteIpAddress}";
    }

    private static string ActorPartitionKey(HttpContext httpContext)
    {
        var actor = httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("sub");
        return actor is null
            ? $"address:{httpContext.Connection.RemoteIpAddress}"
            : $"principal:{actor}";
    }

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
