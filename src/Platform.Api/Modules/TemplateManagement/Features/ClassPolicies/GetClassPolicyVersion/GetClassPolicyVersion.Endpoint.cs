using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class GetClassPolicyVersion
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("/versions/{version:int}", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string application,
        string @class,
        int version,
        Handler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(application, @class, version, cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        httpContext.Response.Headers.ETag = EntityTags.ToHeaderValue(result.Value!.EntityTag);
        return Results.Ok(result.Value);
    }
}
