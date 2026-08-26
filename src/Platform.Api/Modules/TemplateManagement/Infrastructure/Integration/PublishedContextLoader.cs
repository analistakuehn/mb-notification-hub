using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// The single loader of the published context behind the published read
/// contracts that need the template identity together with its published
/// version. It exists so those contracts share one memoized entry instead of
/// each paying its own pair of queries for the same pair, which is what a
/// notification costs when it asks more than one of them about one template.
/// </summary>
internal sealed class PublishedContextLoader(
    TemplateManagementDbContext dbContext,
    PublishedReadCache cache)
{
    /// <summary>
    /// The published context of (application, templateKey), memoized as a
    /// "current published" pointer: a hot read skips the store and converges
    /// on a new publication within the pointer window. The entities are
    /// no-tracking reads of immutable published state, safe to share.
    /// <para>
    /// The identity resolves to its canonical form first, so every caller
    /// agrees on one entry however the request spelled it, and a spelling the
    /// domain refuses stops here without reaching the store and without taking
    /// a slot. Only a success is memoized: a lookup that found nothing has to
    /// see the next publication immediately.
    /// </para>
    /// </summary>
    internal async Task<Result<PublishedTemplateContext>> LoadAsync(
        string application,
        string templateKey,
        CancellationToken cancellationToken)
    {
        Result<string> canonicalApplication = ApplicationName.Create(application);
        if (canonicalApplication.IsFailure)
        {
            return canonicalApplication.AsFailure<string, PublishedTemplateContext>();
        }

        Result<TemplateKey> canonicalKey = TemplateKey.Create(templateKey);
        if (canonicalKey.IsFailure)
        {
            return canonicalKey.AsFailure<TemplateKey, PublishedTemplateContext>();
        }

        var app = canonicalApplication.Value!;
        var key = canonicalKey.Value!.Value;

        // The prefix names the value, not the caller: renaming it would fork
        // the entry the published read contracts exist to share.
        var cacheKey = $"render-context:{app}:{key}";
        if (cache.TryGetPointer(cacheKey, out PublishedTemplateContext cached))
        {
            return Result.Success(cached);
        }

        Result<PublishedTemplateContext> loaded = await dbContext.FindPublishedTemplateAsync(
            app, key, cancellationToken);
        if (loaded.IsSuccess)
        {
            cache.SetPointer(cacheKey, loaded.Value!);
        }

        return loaded;
    }
}
