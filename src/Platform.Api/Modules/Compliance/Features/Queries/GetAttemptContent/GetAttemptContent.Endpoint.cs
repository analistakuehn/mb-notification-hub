using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NotificationHub.Api.Modules.Compliance.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Queries;

internal static partial class GetAttemptContent
{
    internal static void MapEndpoint(RouteGroupBuilder group)
    {
        RouteHandlerBuilder route = group.MapGet("/notifications/{id}/attempts/{seq:int}/content", HandleHttpAsync)
            .RequireAuthorization(ComplianceAuthorizationSetup.AuditPolicyName)
            .RequireRateLimiting(ComplianceRateLimitingSetup.ContentPolicyName)
            .WithRequestLogging();
        route.WithDescription(AuditReadContract.ContentFormNotice + " " + AuditReadContract.DisclosureNotice);
    }

    private static async Task<IResult> HandleHttpAsync(
        string id,
        int seq,
        HttpContext httpContext,
        Handler handler,
        AuditAccessLog accessLog,
        CancellationToken cancellationToken)
    {
        if (!NotificationIdentity.TryParse(id, out Guid notificationId))
        {
            return AuditProblems.InvalidRequest(
                "O identificador precisa estar na forma pública publicada pela ingestão.");
        }

        if (seq < 1)
        {
            return AuditProblems.InvalidRequest("A sequência da tentativa começa em 1.");
        }

        var actor = new DisclosureActor(
            AuditPrincipal.Of(httpContext.User), AuditPrincipal.RouteOf(httpContext));
        Result<Response> result = await handler.HandleAsync(
            new AttemptContentQuery(notificationId, seq, actor), cancellationToken);
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        if (result.ErrorKind == ResultErrorKind.NotFound)
        {
            accessLog.RecordSubjectNotFound(httpContext);
            return AuditProblems.NotFound();
        }

        return AuditProblems.DisclosureUnavailable();
    }
}
