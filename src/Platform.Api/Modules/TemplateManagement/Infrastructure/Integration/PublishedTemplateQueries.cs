using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>Template identity plus its published version, loaded for a published contract call.</summary>
internal sealed record PublishedTemplateContext(Template Template, TemplateVersion Version);

/// <summary>
/// Shared lookup behind the published contract services: resolves the
/// template of an application, refuses an identity that rejects new requests,
/// and loads the single published version.
/// </summary>
internal static class PublishedTemplateQueries
{
    internal static async Task<Result<PublishedTemplateContext>> FindPublishedTemplateAsync(
        this TemplateManagementDbContext dbContext,
        string application,
        string templateKey,
        CancellationToken cancellationToken)
    {
        Result<(Template Template, TemplateKey Key)> template =
            await dbContext.FindApplicationTemplateAsync(application, templateKey, cancellationToken);
        if (template.IsFailure)
        {
            return template.AsFailure<(Template, TemplateKey), PublishedTemplateContext>();
        }

        if (template.Value.Template.Status != TemplateStatus.Active)
        {
            Result rejection = RejectedIdentity(template.Value.Template);
            return rejection.AsFailure<PublishedTemplateContext>();
        }

        Result<TemplateVersion> published =
            await dbContext.FindPublishedVersionAsync(template.Value.Key, cancellationToken);
        return published.IsFailure
            ? published.AsFailure<TemplateVersion, PublishedTemplateContext>()
            : Result.Success(new PublishedTemplateContext(template.Value.Template, published.Value!));
    }

    /// <summary>Loads the template identity, refusing a key the application does not own.</summary>
    internal static async Task<Result<(Template Template, TemplateKey Key)>> FindApplicationTemplateAsync(
        this TemplateManagementDbContext dbContext,
        string application,
        string templateKey,
        CancellationToken cancellationToken)
    {
        Result<string> applicationName = ApplicationName.Create(application);
        if (applicationName.IsFailure)
        {
            return applicationName.AsFailure<string, (Template, TemplateKey)>();
        }

        Result<TemplateKey> key = TemplateKey.Create(templateKey);
        if (key.IsFailure)
        {
            return key.AsFailure<TemplateKey, (Template, TemplateKey)>();
        }

        Template? template = await dbContext.Templates
            .AsNoTracking()
            .WhereKey(key.Value!)
            .FirstOrDefaultAsync(cancellationToken);
        if (template is null || !string.Equals(template.Application, applicationName.Value!, StringComparison.Ordinal))
        {
            return Result.NotFound<(Template, TemplateKey)>(DomainError.Format(
                ErrorCodes.TemplateNotFound,
                $"Application '{applicationName.Value}' has no template '{key.Value!.Value}'."));
        }

        return Result.Success((template, key.Value!));
    }

    internal static async Task<Result<TemplateVersion>> FindPublishedVersionAsync(
        this TemplateManagementDbContext dbContext,
        TemplateKey key,
        CancellationToken cancellationToken)
    {
        TemplateVersion? published = await dbContext.TemplateVersions
            .AsNoTracking()
            .WhereTemplateKey(key)
            .FirstOrDefaultAsync(candidate => candidate.Status == TemplateVersionStatus.Published, cancellationToken);
        return published is null
            ? Result.NotFound<TemplateVersion>(DomainError.Format(
                ErrorCodes.TemplateVersionNotFound,
                $"Template '{key.Value}' has no published version."))
            : Result.Success(published);
    }

    /// <summary>
    /// The error code carries the catalog rejection reason, so the outcome
    /// stays machine-readable wherever the module decodes its errors.
    /// </summary>
    private static Result RejectedIdentity(Template template)
        => template.Status == TemplateStatus.Disabled
            ? Result.BusinessRuleViolation(DomainError.Format(
                TemplateRejectionReasons.Disabled,
                $"Template '{template.Key.Value}' is disabled and rejects new notification requests."))
            : Result.BusinessRuleViolation(DomainError.Format(
                TemplateRejectionReasons.Deprecated,
                $"Template '{template.Key.Value}' is deprecated and rejects new notification requests."));
}
