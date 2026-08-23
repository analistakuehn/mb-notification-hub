using System.Security.Claims;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutClassPolicyDraft
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/draft", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        [AsParameters] RouteInputs route,
        JsonElement definition,
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

        if (definition.ValueKind != JsonValueKind.Object)
        {
            return ApiResults.Problem(ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.InvalidRequest,
                "The policy definition must be a JSON object."));
        }

        Result<Outcome> result = await handler.HandleAsync(
            new Command(route, definition.GetRawText(), actor.Value!),
            cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        return result.Value switch
        {
            Outcome.Created created => CreatedResult(httpContext, created.Response),
            Outcome.Updated updated => OkResult(httpContext, updated.Response),
            Outcome.Blocked blocked => ApiResults.Problem(
                blocked.Report,
                ErrorCodes.ClassPolicyValidationFailed,
                "The class policy validation blocked this draft. "
                + "The full report travels in the 'checks' extension member."),
            _ => throw new InvalidOperationException("Unsupported draft outcome."),
        };
    }

    private static IResult CreatedResult(HttpContext httpContext, Response response)
    {
        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(response.EntityTag);
        return Results.Created(
            $"/v1/applications/{response.Application}/classes/{response.Class}/policy/versions/{response.Version}",
            response);
    }

    private static IResult OkResult(HttpContext httpContext, Response response)
    {
        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(response.EntityTag);
        return Results.Ok(response);
    }
}
