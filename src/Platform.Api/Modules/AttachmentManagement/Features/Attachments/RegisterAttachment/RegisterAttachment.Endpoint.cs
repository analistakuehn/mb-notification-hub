using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("", HandleHttpAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .AddEndpointFilter(AuthorizeApplicationAsync)
            .WithValidation<Command>()
            .WithRequestLogging()
            .Produces<Response>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async ValueTask<object?> AuthorizeApplicationAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        Command command = context.Arguments.OfType<Command>().Single();
        IAuthorizationService authorizationService = context.HttpContext.RequestServices
            .GetRequiredService<IAuthorizationService>();
        AuthorizationResult authorization = await authorizationService.AuthorizeAsync(
            context.HttpContext.User,
            new AttachmentAuthorizationResource.Application(
                command.Application,
                context.HttpContext.RequestAborted),
            AuthorizationSetup.ProducerPolicyName);
        if (!authorization.Succeeded)
        {
            Result<Response> failure = AuthorizationSetup.IsUnavailable(authorization)
                ? Result.IntegrationFailure<Response>(ErrorCodes.AuthorizationUnavailable)
                : Result.Forbidden<Response>(ErrorCodes.AccessDenied);
            return ApiResults.Problem(failure);
        }

        return await next(context);
    }

    private static async Task<IResult> HandleHttpAsync(
        Command command,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<Response> result = await handler.HandleAsync(command, cancellationToken);
        return result.IsSuccess
            ? Results.Created(
                $"/v1/attachments/{result.Value!.Reference}",
                result.Value)
            : ApiResults.Problem(result);
    }
}
