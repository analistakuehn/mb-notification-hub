using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Published catalog reads backed by this module's store. Only published state
/// crosses the boundary, always as contract values, never as domain entities.
/// </summary>
internal sealed class PublishedCatalog(TemplateManagementDbContext dbContext) : IPublishedCatalog
{
    public async Task<Result<PublishedTemplateLookup>> FindTemplateAsync(
        string application,
        string templateKey,
        CancellationToken cancellationToken)
    {
        Result<(Template Template, TemplateKey Key)> lookup =
            await dbContext.FindApplicationTemplateAsync(application, templateKey, cancellationToken);
        if (lookup.IsFailure)
        {
            return lookup.AsFailure<(Template, TemplateKey), PublishedTemplateLookup>();
        }

        (Template template, TemplateKey key) = lookup.Value;
        if (template.Status == TemplateStatus.Deprecated)
        {
            return Result.Success<PublishedTemplateLookup>(
                new PublishedTemplateLookup.Rejected(TemplateRejectionReasons.Deprecated));
        }

        if (template.Status == TemplateStatus.Disabled)
        {
            return Result.Success<PublishedTemplateLookup>(
                new PublishedTemplateLookup.Rejected(TemplateRejectionReasons.Disabled));
        }

        Result<TemplateVersion> published = await dbContext.FindPublishedVersionAsync(key, cancellationToken);
        if (published.IsFailure)
        {
            return published.AsFailure<TemplateVersion, PublishedTemplateLookup>();
        }

        TemplateVersion version = published.Value!;
        return Result.Success<PublishedTemplateLookup>(new PublishedTemplateLookup.Published(new PublishedTemplate
        {
            Application = template.Application,
            TemplateKey = key.Value,
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            SensitiveVariables = [.. template.SensitiveVariables],
            ChannelsWithContent = version.Contents
                .Select(content => content.Channel)
                .Distinct()
                .ToList(),
            DefaultLocale = template.DefaultLocale?.Value,
            Version = version.Version,
            ContentHash = version.ContentHash,
        }));
    }

    public async Task<Result<PublishedClassPolicy>> FindClassPolicyAsync(
        string application,
        string notificationClass,
        CancellationToken cancellationToken)
    {
        Result<string> applicationName = ApplicationName.Create(application);
        if (applicationName.IsFailure)
        {
            return applicationName.AsFailure<string, PublishedClassPolicy>();
        }

        Result<NotificationClass> policyClass = NotificationClasses.Create(notificationClass);
        if (policyClass.IsFailure)
        {
            return policyClass.AsFailure<NotificationClass, PublishedClassPolicy>();
        }

        var app = applicationName.Value!;
        NotificationClass targetClass = policyClass.Value;
        ClassPolicyVersion? published = await dbContext.ClassPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Application == app
                    && candidate.Class == targetClass
                    && candidate.Status == ClassPolicyVersionStatus.Published,
                cancellationToken);
        if (published is null)
        {
            return Result.NotFound<PublishedClassPolicy>(DomainError.Format(
                ErrorCodes.ClassPolicyNotFound,
                $"Application '{app}' has no published policy for class '{targetClass.Canonical()}'."));
        }

        Result<ClassPolicyDefinition> definition = ClassPolicyDefinition.Read(published.DefinitionJson);
        if (definition.IsFailure)
        {
            return definition.AsFailure<ClassPolicyDefinition, PublishedClassPolicy>();
        }

        return Result.Success(new PublishedClassPolicy
        {
            Application = app,
            Class = targetClass.Canonical(),
            Version = published.Version,
            ContentHash = published.ContentHash,
            Definition = definition.Value!,
        });
    }
}
