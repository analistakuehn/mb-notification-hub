using System.Security.Claims;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DeprecateTemplate
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{key}/deprecate", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.PublisherPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Request>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        Request request,
        ClaimsPrincipal principal,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<string> actor = CurrentActor.Identify(principal);
        if (actor.IsFailure)
        {
            return ApiResults.Problem(actor);
        }

        Result<Response> result = await handler.HandleAsync(
            new Command(key, request.Reason, request.Note, actor.Value!),
            cancellationToken);
        return result.IsSuccess ? Results.Ok(result.Value) : ApiResults.Problem(result);
    }
}
