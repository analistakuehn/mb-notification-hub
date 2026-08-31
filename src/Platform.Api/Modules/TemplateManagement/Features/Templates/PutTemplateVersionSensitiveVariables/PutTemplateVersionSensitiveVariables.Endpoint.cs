using System.Security.Claims;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionSensitiveVariables
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/{key}/versions/{version:int}/sensitive-variables", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Request>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        [AsParameters] RouteInputs route,
        Request request,
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
            new Command(route, request, actor.Value!),
            cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(result.Value!.EntityTag);
        return Results.Ok(result.Value);
    }
}
