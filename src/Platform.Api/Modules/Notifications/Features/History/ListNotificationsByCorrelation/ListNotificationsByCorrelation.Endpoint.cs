using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class ListNotificationsByCorrelation
{
    internal static void MapEndpoint(RouteGroupBuilder group)
    {
        RouteHandlerBuilder route = group.MapGet("", HandleHttpAsync)
            .RequireAuthorization(NotificationsAuthorizationSetup.ReadPolicyName)
            .RequireRateLimiting(NotificationsRateLimitingSetup.QueryPolicyName)
            .WithRequestLogging();
        route.WithDescription(NotificationQueryContract.ReplicationLagNotice);
    }

    private static async Task<IResult> HandleHttpAsync(
        [AsParameters] Query query,
        HttpContext httpContext,
        Handler handler,
        NotificationQueryAccessLog accessLog,
        CancellationToken cancellationToken)
    {
        accessLog.RecordAccess(
            httpContext, NotificationQuerySubjects.Correlation, query.CorrelationId ?? string.Empty);

        Result<NotificationHistoryOutcome> result = await handler.HandleAsync(query, cancellationToken);
        return NotificationHistoryResults.From(result);
    }
}
