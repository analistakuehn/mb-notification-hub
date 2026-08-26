using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetLayout
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("/{key}", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        string? versionsCursor,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(key, versionsCursor, cancellationToken);
        return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result);
    }
}
