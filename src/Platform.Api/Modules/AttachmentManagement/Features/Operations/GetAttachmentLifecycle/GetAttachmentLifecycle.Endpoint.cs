using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Operations;

internal static partial class GetAttachmentLifecycle
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("/{reference}", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.OperationsPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging()
            .Produces<Response>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleHttpAsync(
        string reference,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(
            new Request(reference),
            cancellationToken);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ApiResults.Problem(result);
    }
}
