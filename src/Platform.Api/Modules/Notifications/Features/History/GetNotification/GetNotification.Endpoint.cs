using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class GetNotification
{
    internal static void MapEndpoint(RouteGroupBuilder group)
    {
        RouteHandlerBuilder route = group.MapGet("/{id}", HandleHttpAsync)
            .RequireAuthorization(NotificationsAuthorizationSetup.ReadPolicyName)
            .RequireRateLimiting(NotificationsRateLimitingSetup.QueryPolicyName)
            .WithRequestLogging();
        route.WithDescription(NotificationQueryContract.ReplicationLagNotice);
    }

    private static async Task<IResult> HandleHttpAsync(
        string id,
        HttpContext httpContext,
        Handler handler,
        NotificationQueryAccessLog accessLog,
        CancellationToken cancellationToken)
    {
        accessLog.RecordAccess(httpContext, NotificationQuerySubjects.Notification, id);

        // A malformed identity never reaches the store: it is a bad request,
        // and answering it as a miss would let a caller tell a wrong shape
        // from a shape that simply does not exist.
        if (!NotificationId.TryParse(id, out Guid notificationId))
        {
            return QueryProblems.InvalidRequest(
                "O identificador precisa estar na forma pública publicada pela ingestão.");
        }

        Result<Response> result = await handler.HandleAsync(notificationId, cancellationToken);
        return result.IsSuccess ? Results.Ok(result.Value) : QueryProblems.NotFound();
    }
}
