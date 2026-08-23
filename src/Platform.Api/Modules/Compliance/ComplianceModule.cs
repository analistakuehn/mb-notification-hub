using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Compliance.Features.Queries;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NotificationHub.Api.Modules.Compliance.Infrastructure.RateLimiting;

namespace NotificationHub.Api.Modules.Compliance;

/// <summary>
/// The audit surface, composed from published contracts alone. This module owns
/// no persistence and registers no store: everything it serves is read through
/// the published surface of the module that owns the data, and the only thing it
/// writes is the disclosure record of its own answers.
/// </summary>
public sealed class ComplianceModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddComplianceAuthorization();
        services.AddComplianceRateLimiting();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthorizationHandler, AuditAccessHandler>();

        services.AddOptions<ContentDisclosureAlarmOptions>()
            .Bind(configuration.GetSection(ContentDisclosureAlarmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ContentDisclosureAlarm>();
        services.AddSingleton<AuditAccessLog>();

        services.AddScoped<DisclosureRecorder>();
        services.AddScoped<GetNotificationEvidence.Handler>();
        services.AddScoped<GetAttemptContent.Handler>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder audit = app.MapGroup("/v1/audit");
        GetNotificationEvidence.MapEndpoint(audit);
        GetAttemptContent.MapEndpoint(audit);
    }
}
