using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class GetClassPolicy
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("/", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string application,
        string @class,
        Handler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(application, @class, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        if (result.Value!.DraftEntityTag is string draftEntityTag)
        {
            httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(draftEntityTag);
        }

        return Results.Ok(result.Value);
    }
}
