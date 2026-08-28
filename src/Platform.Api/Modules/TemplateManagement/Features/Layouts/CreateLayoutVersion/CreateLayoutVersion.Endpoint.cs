using System.Security.Claims;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class CreateLayoutVersion
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{key}/versions", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        Request? request,
        ClaimsPrincipal principal,
        Handler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        Result<string> actor = CurrentActor.Identify(principal);
        if (actor.IsFailure)
        {
            return ApiResults.Problem(actor);
        }

        Result<Response> result = await handler.HandleAsync(
            new Command(key, request?.FromVersion, actor.Value!),
            cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        Response response = result.Value!;
        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(response.EntityTag);
        return Results.Created(
            $"/v1/layouts/{response.LayoutKey}/versions/{response.Version}",
            response);
    }
}
