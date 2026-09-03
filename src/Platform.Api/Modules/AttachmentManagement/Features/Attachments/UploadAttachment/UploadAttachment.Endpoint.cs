using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class UploadAttachment
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/{reference}/content", HandleHttpAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .AddEndpointFilter(LimitBodyAsync)
            .WithRequestLogging()
            .Accepts<Stream>("application/octet-stream")
            .Produces<Response>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async ValueTask<object?> LimitBodyAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        IHttpMaxRequestBodySizeFeature? feature =
            context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = Attachment.MaxSizeBytes;
        }

        return await next(context);
    }

    private static async Task<IResult> HandleHttpAsync(
        string reference,
        HttpRequest request,
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
            new Command(reference, request.Body, request.ContentLength),
            cancellationToken);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ApiResults.Problem(result);
    }
}
