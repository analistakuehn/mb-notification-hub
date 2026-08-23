using System.Security.Claims;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>Kinds of subject a query route can be asked about.</summary>
internal static class NotificationQuerySubjects
{
    internal const string Notification = "notification";
    internal const string Recipient = "recipient";
    internal const string Correlation = "correlation";
}

/// <summary>
/// Structured record of who read what on the query surface. It is a log, not
/// an audit entry, and that is the decision: appending a trail row per read
/// would serialize every query against the ingestion on the chain's advisory
/// lock, and the <c>audit.read</c> action belongs to the audit routes, which
/// are where full content and contact data actually leave the hub.
/// </summary>
internal sealed class NotificationQueryAccessLog(ILogger<NotificationQueryAccessLog> logger)
{
    internal void RecordAccess(HttpContext httpContext, string subjectType, string subjectId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var principal = httpContext.User.FindFirstValue("appid")
            ?? httpContext.User.FindFirstValue("oid")
            ?? httpContext.User.FindFirstValue("sub")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "(desconhecido)";
        var route = httpContext.GetEndpoint()?.DisplayName ?? httpContext.Request.Path.Value ?? "(desconhecida)";

        logger.NotificationQueryServed(principal, route, subjectType, subjectId);
    }
}
