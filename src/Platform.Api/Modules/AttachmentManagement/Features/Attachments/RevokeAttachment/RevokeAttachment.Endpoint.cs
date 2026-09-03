using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RevokeAttachment
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{reference}/revocation", HandleHttpAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Command>()
            .WithRequestLogging()
            .Produces<Response>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> HandleHttpAsync(
        string reference,
        Command command,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        Handler handler,
        CancellationToken cancellationToken)
    {
        AuthorizationResult authorization = await authorizationService.AuthorizeAsync(
            httpContext.User,
            new AttachmentAuthorizationResource.Reference(reference, cancellationToken),
            AuthorizationSetup.ProducerPolicyName);
        if (!authorization.Succeeded)
        {
            Result<Response> failure = AuthorizationSetup.IsUnavailable(authorization)
                ? Result.IntegrationFailure<Response>(ErrorCodes.AuthorizationUnavailable)
                : Result.NotFound<Response>(ErrorCodes.NotFound);
            return ApiResults.Problem(failure);
        }

        Result<Response> result = await handler.HandleAsync(
            reference,
            command,
            cancellationToken);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ApiResults.Problem(result);
    }
}
