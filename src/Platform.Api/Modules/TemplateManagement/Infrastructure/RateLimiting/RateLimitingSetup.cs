using Microsoft.AspNetCore.RateLimiting;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;

/// <summary>
/// Named rate-limit policy attached to every TemplateManagement endpoint.
/// Authoring is a low-volume administrative surface; the window is generous and
/// exists to stop scripted abuse, not to shape normal traffic.
/// </summary>
public static class RateLimitingSetup
{
    public const string PolicyName = "template-management";

    public static IServiceCollection AddTemplateManagementRateLimiting(this IServiceCollection services)
        => services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter(PolicyName, limiter =>
            {
                limiter.PermitLimit = 1000;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            }));
}
