namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// Structured record of the reads that disclose nothing. A subject that does not
/// exist leaves a security log and never a trail row: nothing was disclosed, so
/// there is nothing for the hash chain to vouch for, and a row per miss would
/// let a sweep of identities fatten the chain at no cost to the sweeper.
/// </summary>
internal sealed class AuditAccessLog(ILogger<AuditAccessLog> logger)
{
    internal void RecordSubjectNotFound(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        logger.AuditSubjectNotFound(
            AuditPrincipal.Of(httpContext.User), AuditPrincipal.RouteOf(httpContext));
    }
}
