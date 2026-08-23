using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

/// <summary>
/// Cache-aside layer of the published directory read, behind the contract
/// and inside this module. A fresh entry answers without touching the store;
/// a stale or absent entry reads the store and rewrites the entry. When the
/// store read fails, only a caller that declared the last-known fallback
/// receives the cached snapshot, stale or fresh; every other caller sees the
/// failure and its queue retry owns the degradation. The reveal read is
/// never cached: plaintext contact values do not belong in a cache.
/// </summary>
internal sealed class CachedRecipientDirectory(
    RecipientDirectory store,
    RecipientSnapshotCache cache,
    ILogger<CachedRecipientDirectory> logger) : IRecipientDirectory
{
    public Task<Result<RecipientSnapshot>> FindAsync(string recipientId, CancellationToken cancellationToken)
        => FindAsync(recipientId, RecipientReadFallback.None, cancellationToken);

    public async Task<Result<RecipientSnapshot>> FindAsync(
        string recipientId,
        RecipientReadFallback fallback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        CachedRecipientSnapshot? cached = await cache.FindAsync(recipientId, cancellationToken);
        if (cached is { Stale: false })
        {
            return Result.Success(cached.Snapshot);
        }

        Result<RecipientSnapshot> fromStore;
        try
        {
            fromStore = await store.FindAsync(recipientId, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && fallback == RecipientReadFallback.LastKnown
            && cached is not null)
        {
            logger.ServedLastKnownSnapshot(recipientId, cached.CachedAt, exception);
            return Result.Success(cached.Snapshot);
        }

        if (fromStore.IsSuccess)
        {
            await cache.StoreAsync(fromStore.Value!, cancellationToken);
        }

        return fromStore;
    }

    public Task<Result<string>> RevealContactValueAsync(
        string recipientId,
        Guid contactPointId,
        CancellationToken cancellationToken)
        => store.RevealContactValueAsync(recipientId, contactPointId, cancellationToken);
}
