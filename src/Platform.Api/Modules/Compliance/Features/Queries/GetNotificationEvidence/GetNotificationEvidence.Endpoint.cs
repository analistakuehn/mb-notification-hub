using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NotificationHub.Api.Modules.Compliance.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Queries;

internal static partial class GetNotificationEvidence
{
    internal static void MapEndpoint(RouteGroupBuilder group)
    {
        RouteHandlerBuilder route = group.MapGet("/notifications/{id}", HandleHttpAsync)
            .RequireAuthorization(ComplianceAuthorizationSetup.AuditPolicyName)
            .RequireRateLimiting(ComplianceRateLimitingSetup.EvidencePolicyName)
            .WithRequestLogging();
        route.WithDescription(
            AuditReadContract.DisclosureNotice + " "
            + AuditReadContract.PriorAccessNotice + " "
            + AuditReadContract.ProviderDeliveryNotice + " "
            + AuditReadContract.DeviceInvalidationReasonNotice);
    }

    private static async Task<IResult> HandleHttpAsync(
        string id,
        HttpContext httpContext,
        Handler handler,
        AuditAccessLog accessLog,
        CancellationToken cancellationToken)
    {
        // A malformed identity never reaches a store: it is a bad request, and
        // answering it as a miss would let a caller tell a wrong shape from a
        // shape that simply does not exist.
        if (!NotificationIdentity.TryParse(id, out Guid notificationId))
        {
            return AuditProblems.InvalidRequest(
                "O identificador precisa estar na forma pública publicada pela ingestão.");
        }

        var actor = new DisclosureActor(
            AuditPrincipal.Of(httpContext.User), AuditPrincipal.RouteOf(httpContext));
        Result<Response> result = await handler.HandleAsync(notificationId, actor, cancellationToken);
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        // A subject that does not exist disclosed nothing, so it leaves a
        // security log and no trail row.
        if (result.ErrorKind == ResultErrorKind.NotFound)
        {
            accessLog.RecordSubjectNotFound(httpContext);
            return AuditProblems.NotFound();
        }

        return AuditProblems.DisclosureUnavailable();
    }
}
