using System.Security.Claims;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionVariablesSchema
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/{key}/versions/{version:int}/variables-schema", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        [AsParameters] RouteInputs route,
        JsonElement schema,
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

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return ApiResults.Problem(ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.InvalidRequest,
                "The variables schema must be a JSON object."));
        }

        Result<Response> result = await handler.HandleAsync(
            new Command(route, schema.GetRawText(), actor.Value!),
            cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(result.Value!.EntityTag);
        return Results.Ok(result.Value);
    }
}
