using System.Security.Claims;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishTemplateVersion
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{key}/versions/{version:int}/publish", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.PublisherPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        int version,
        ClaimsPrincipal principal,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<string> actor = CurrentActor.Identify(principal);
        if (actor.IsFailure)
        {
            return ApiResults.Problem(actor);
        }

        Result<Outcome> result = await handler.HandleAsync(key, version, actor.Value!, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        return result.Value switch
        {
            Outcome.Published published => Results.Ok(published.Response),
            Outcome.Blocked blocked => ApiResults.Problem(
                blocked.Report,
                ErrorCodes.TemplateValidationFailed,
                $"The integral validation blocked the publication of version {version}. "
                + "The full report travels in the 'checks' extension member."),
            _ => throw new InvalidOperationException("Unsupported publish outcome."),
        };
    }
}
