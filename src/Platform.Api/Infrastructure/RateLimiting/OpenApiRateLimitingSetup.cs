using System.Security.Claims;
using System.Threading.RateLimiting;

namespace NotificationHub.Api.Infrastructure.RateLimiting;

/// <summary>
/// Named rate-limit policy of the OpenAPI document route, the one endpoint the
/// host owns directly instead of delegating to a module.
/// <para>
/// The document is the machine contract producers and administrative clients
/// generate their clients from, so it stays reachable in every environment.
/// That makes it worth a ceiling: the response is built per request rather
/// than served from a cached buffer, and it is the largest single body the API
/// returns, so an unbounded route here hands one principal a cheap way to burn
/// host CPU and egress. A client regenerates its bindings on a release
/// cadence, never in a loop, so the budget is deliberately small.
/// </para>
/// <para>
/// The partition mirrors the identity chain the modules use (<c>oid</c>, then
/// <c>sub</c>, then the mapped name identifier), resolved from the raw claims
/// because the host must not depend on a module. An anonymous request never
/// reaches the limiter with a principal, so it falls back to an address
/// partition and cannot consume an authenticated budget.
/// </para>
/// </summary>
public static class OpenApiRateLimitingSetup
{
    public const string PolicyName = "openapi-document";

    private const int PermitLimit = 60;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddOpenApiRateLimiting(this IServiceCollection services)
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

    private static string PartitionKey(HttpContext httpContext)
    {
        var actorId = httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrWhiteSpace(actorId)
            ? $"address:{httpContext.Connection.RemoteIpAddress}"
            : $"principal:{actorId}";
    }
}
