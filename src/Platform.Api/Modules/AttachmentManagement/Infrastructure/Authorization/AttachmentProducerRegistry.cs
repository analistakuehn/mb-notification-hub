using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

internal enum AttachmentAuthorizationDecision
{
    Allowed,
    Denied,
    Unavailable,
}

internal interface IAttachmentProducerRegistry
{
    Task<AttachmentAuthorizationDecision> AuthorizeAsync(
        AttachmentPrincipal principal,
        AttachmentAuthorizationResource resource,
        CancellationToken cancellationToken);
}

internal sealed class AttachmentProducerRegistry(
    AttachmentManagementDbContext dbContext,
    ILogger<AttachmentProducerRegistry> logger)
    : IAttachmentProducerRegistry
{
    public async Task<AttachmentAuthorizationDecision> AuthorizeAsync(
        AttachmentPrincipal principal,
        AttachmentAuthorizationResource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var allowed = resource switch
            {
                AttachmentAuthorizationResource.Application application =>
                    await AllowsApplicationAsync(
                        principal,
                        application.Name,
                        cancellationToken),
                AttachmentAuthorizationResource.Reference reference =>
                    await AllowsReferenceAsync(
                        principal,
                        reference.Value,
                        cancellationToken),
                _ => false,
            };
            return allowed
                ? AttachmentAuthorizationDecision.Allowed
                : AttachmentAuthorizationDecision.Denied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.RegistryUnavailable(exception);
            return AttachmentAuthorizationDecision.Unavailable;
        }
    }

    private Task<bool> AllowsApplicationAsync(
        AttachmentPrincipal principal,
        string application,
        CancellationToken cancellationToken)
        => dbContext.ProducerApplicationGrants
            .AsNoTracking()
            .AnyAsync(
                grant => grant.Issuer == principal.Issuer
                    && grant.ClaimKind == principal.ClaimKind
                    && grant.PrincipalId == principal.PrincipalId
                    && grant.Application == application,
                cancellationToken);

    private async Task<bool> AllowsReferenceAsync(
        AttachmentPrincipal principal,
        string value,
        CancellationToken cancellationToken)
    {
        Result<AttachmentReference> reference = AttachmentReference.Create(value);
        if (reference.IsFailure)
        {
            return false;
        }

        return await dbContext.Attachments
            .AsNoTracking()
            .AnyAsync(
                attachment => attachment.Reference == reference.Value
                    && dbContext.ProducerApplicationGrants.Any(grant =>
                        grant.Issuer == principal.Issuer
                        && grant.ClaimKind == principal.ClaimKind
                        && grant.PrincipalId == principal.PrincipalId
                        && grant.Application == attachment.Application),
                cancellationToken);
    }
}
