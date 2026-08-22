using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Command>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        Command command,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(command, cancellationToken);
        return result.IsSuccess
            ? Results.Created($"/v1/templates/{result.Value!.Key}", result.Value)
            : ApiResults.Problem(result);
    }
}
