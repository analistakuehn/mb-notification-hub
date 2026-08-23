using System.Security.Claims;
using System.Threading.RateLimiting;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;

/// <summary>
/// Named in-process rate-limit policy attached to the contact and consent
/// write endpoints as a coarse backstop against scripted abuse by one
/// principal. Contact writes are low-volume registration traffic, so a fixed
/// window per principal is enough; there is no business rate limit on this
/// surface.
/// </summary>
public static class ContactConsentRateLimitingSetup
{
    public const string PolicyName = "contacts-write";

    private const int PermitLimit = 600;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddContactConsentRateLimiting(this IServiceCollection services)
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
        var principal = httpContext.User.FindFirstValue("appid")
            ?? httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("sub");
        return principal is null
            ? $"address:{httpContext.Connection.RemoteIpAddress}"
            : $"principal:{principal}";
    }
}
