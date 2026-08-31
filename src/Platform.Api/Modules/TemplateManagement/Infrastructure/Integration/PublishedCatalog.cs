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
/// Successful lookups memoize as "current published" pointers: the hot path
/// re-reads them from memory and converges on a new publication within the
/// pointer window.
/// </summary>
internal sealed class PublishedCatalog(
    TemplateManagementDbContext dbContext,
    PublishedReadCache cache) : IPublishedCatalog
{
    /// <summary>
    /// Resolves the identity to its canonical form before anything else, so one
    /// published template owns exactly one entry however the caller spelled the
    /// request, and a spelling the domain refuses never reaches the store and
    /// never occupies a slot.
    /// </summary>
    public async Task<Result<PublishedTemplateLookup>> FindTemplateAsync(
        string application,
        string templateKey,
        CancellationToken cancellationToken)
    {
        Result<string> canonicalApplication = ApplicationName.Create(application);
        if (canonicalApplication.IsFailure)
        {
            return canonicalApplication.AsFailure<string, PublishedTemplateLookup>();
        }

        Result<TemplateKey> canonicalKey = TemplateKey.Create(templateKey);
        if (canonicalKey.IsFailure)
        {
            return canonicalKey.AsFailure<TemplateKey, PublishedTemplateLookup>();
        }

        var app = canonicalApplication.Value!;
        var key = canonicalKey.Value!.Value;
        var cacheKey = PublishedPointerKeys.Template(app, key);
        if (cache.TryGetPointer(cacheKey, out PublishedTemplateLookup cached))
        {
            return Result.Success(cached);
        }

        // The fence is read before the query leaves: a lifecycle transition
        // that commits while this load is in flight refuses the write below
        // instead of having the superseded value put back on top of it.
        var generation = cache.Generation;
        Result<PublishedTemplateLookup> lookedUp =
            await FindTemplateFromStoreAsync(app, key, cancellationToken);
        if (lookedUp.IsSuccess)
        {
            cache.SetPointerIfCurrent(cacheKey, lookedUp.Value!, generation);
        }

        return lookedUp;
    }

    private async Task<Result<PublishedTemplateLookup>> FindTemplateFromStoreAsync(
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
            SensitiveVariables = [.. version.SensitiveVariables],
            ChannelsWithContent = version.Contents
                .Select(content => content.Channel)
                .Distinct()
                .ToList(),
            DefaultLocale = template.DefaultLocale?.Value,
            Version = version.Version,
            ContentHash = version.ContentHash,
        }));
    }

    /// <summary>
    /// Same canonical-first shape as the template lookup, over the pair the
    /// policy query itself keys on: the class resolves to one of the three
    /// accepted values, so the entry cannot fork on how the caller wrote it.
    /// </summary>
    public async Task<Result<PublishedClassPolicy>> FindClassPolicyAsync(
        string application,
        string notificationClass,
        CancellationToken cancellationToken)
    {
        Result<string> canonicalApplication = ApplicationName.Create(application);
        if (canonicalApplication.IsFailure)
        {
            return canonicalApplication.AsFailure<string, PublishedClassPolicy>();
        }

        Result<NotificationClass> canonicalClass = NotificationClasses.Create(notificationClass);
        if (canonicalClass.IsFailure)
        {
            return canonicalClass.AsFailure<NotificationClass, PublishedClassPolicy>();
        }

        var app = canonicalApplication.Value!;
        var policyClass = canonicalClass.Value.Canonical();
        var cacheKey = PublishedPointerKeys.ClassPolicy(app, policyClass);
        if (cache.TryGetPointer(cacheKey, out PublishedClassPolicy cached))
        {
            return Result.Success(cached);
        }

        var generation = cache.Generation;
        Result<PublishedClassPolicy> published =
            await FindClassPolicyFromStoreAsync(app, policyClass, cancellationToken);
        if (published.IsSuccess)
        {
            cache.SetPointerIfCurrent(cacheKey, published.Value!, generation);
        }

        return published;
    }

    private async Task<Result<PublishedClassPolicy>> FindClassPolicyFromStoreAsync(
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
