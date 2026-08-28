using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersion
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{key}/versions/{version:int}/render", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Request>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        int version,
        Request request,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(key, version, request, cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result) : Results.Ok(result.Value);
    }
}
