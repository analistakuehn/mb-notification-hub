using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ListLayouts
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        [AsParameters] Query query,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(query, cancellationToken);
        return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result);
    }
}
