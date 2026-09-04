using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

internal sealed class AttachmentProducerRequirement : IAuthorizationRequirement;

internal sealed class AttachmentProducerAuthorizationHandler(
    IAttachmentProducerRegistry registry)
    : AuthorizationHandler<AttachmentProducerRequirement, AttachmentAuthorizationResource>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AttachmentProducerRequirement requirement,
        AttachmentAuthorizationResource resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = AttachmentPrincipal.Resolve(context.User);
        if (principal is null)
        {
            Deny(context, ErrorCodes.AccessDenied);
            return;
        }

        AttachmentAuthorizationDecision decision = await registry.AuthorizeAsync(
            principal,
            resource,
            resource.CancellationToken);
        switch (decision)
        {
            case AttachmentAuthorizationDecision.Allowed:
                context.Succeed(requirement);
                break;
            case AttachmentAuthorizationDecision.Unavailable:
                Deny(context, ErrorCodes.AuthorizationUnavailable);
                break;
            default:
                Deny(context, ErrorCodes.AccessDenied);
                break;
        }
    }

    private void Deny(AuthorizationHandlerContext context, string reason)
        => context.Fail(new AuthorizationFailureReason(this, reason));
}
