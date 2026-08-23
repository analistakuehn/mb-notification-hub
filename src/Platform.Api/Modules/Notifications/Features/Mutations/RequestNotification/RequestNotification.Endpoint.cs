using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const int MaxIdempotencyKeyLength = 200;

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("", HandleHttpAsync)
            .RequireAuthorization(NotificationsAuthorizationSetup.SendPolicyName)
            .RequireRateLimiting(NotificationsRateLimitingSetup.PolicyName)
            .WithValidation<Command>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        Command command,
        HttpContext httpContext,
        Handler handler,
        CancellationToken cancellationToken)
    {
        string? idempotencyKey = httpContext.Request.Headers[IdempotencyKeyHeader];
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return IngestionProblems.MissingIdempotencyKey();
        }

        var producer = ProducerIdentity.Identify(httpContext.User);
        if (producer is null)
        {
            return IngestionProblems.ClassNotAllowed(command.Class);
        }

        IReadOnlySet<string> producerRoles = httpContext.User
            .FindAll(claim => claim.Type == "role")
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);

        Result<Outcome> result = await handler.HandleAsync(
            command,
            producer,
            RestProducerAuthorizer.Authorize(producerRoles, command.Class),
            IngestionOrigin.Rest,
            idempotencyKey,
            cancellationToken);
        if (result.IsFailure)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return result.Value switch
        {
            Outcome.Accepted accepted => AcceptedResponse(accepted.NotificationId),
            Outcome.Replayed replayed => Results.Ok(new Response(
                NotificationId.Format(replayed.NotificationId), NotificationStatuses.Accepted)),
            Outcome.IdempotencyConflict => IngestionProblems.IdempotencyConflict(),
            Outcome.ProducerNotAuthorized => IngestionProblems.ClassNotAllowed(command.Class),
            Outcome.TemplateRejected rejected => IngestionProblems.TemplateRejection(
                rejected.Reason, rejected.Detail, rejected.Checks),
            Outcome.RateLimited limited => IngestionProblems.RateLimited(limited.RetryAfterSeconds),
            // Unreachable over this route: the validation filter answers the
            // published 400 before the use case runs. Mapped to the same shape
            // so a filter that ever stops running changes no contract.
            Outcome.PayloadInvalid invalid => Results.ValidationProblem(invalid.Errors),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult AcceptedResponse(Guid notificationId)
    {
        var publicId = NotificationId.Format(notificationId);
        return Results.Accepted(
            $"/v1/notifications/{publicId}",
            new Response(publicId, NotificationStatuses.Accepted));
    }
}
