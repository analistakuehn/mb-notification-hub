using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;

/// <summary>
/// Decides the audit requirement and records the decision. The grant is logged
/// because a security review reads the two lines together; the denial is logged
/// because a refused access is exactly the signal an insider investigation looks
/// for, and it is the only record it leaves.
/// </summary>
internal sealed class AuditAccessHandler(ILogger<AuditAccessHandler> logger)
    : AuthorizationHandler<AuditAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AuditAccessRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = AuditPrincipal.Of(context.User);
        var route = context.Resource is HttpContext httpContext
            ? AuditPrincipal.RouteOf(httpContext)
            : "(desconhecida)";

        if (context.User.IsInRole(ComplianceAuthorizationSetup.AuditRole))
        {
            logger.AuditAccessGranted(principal, route);
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        logger.AuditAccessDenied(principal, route);
        context.Fail(new AuthorizationFailureReason(this, "O principal não porta o papel de auditoria."));
        return Task.CompletedTask;
    }
}
