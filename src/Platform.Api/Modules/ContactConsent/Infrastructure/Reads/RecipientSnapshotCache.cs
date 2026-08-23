using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Redis;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

/// <summary>One cached snapshot with its freshness state.</summary>
internal sealed record CachedRecipientSnapshot(RecipientSnapshot Snapshot, DateTimeOffset CachedAt, bool Stale);

/// <summary>
/// Encrypted Redis store of recipient snapshots, entirely inside this module:
/// entries are sealed with the module's dedicated key scope, so contact data
/// never sits in Redis in the clear nor under an application key. An
/// invalidation marks the entry stale instead of deleting it: a stale entry
/// forces the next read back to the store, yet stays available as the last
/// known value for the flows that tolerate it. Every Redis failure fails
/// open: the store answers.
/// </summary>
internal sealed class RecipientSnapshotCache(
    ContactConsentRedisConnection redis,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider,
    ILogger<RecipientSnapshotCache> logger)
{
    public async Task<CachedRecipientSnapshot?> FindAsync(
        string recipientId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RedisValue sealedEntry = await redis.Database.StringGetAsync(Key(recipientId));
            if (sealedEntry.IsNullOrEmpty)
            {
                return null;
            }

            var plaintext = await cipher.DecryptAsync(
                ContactValueProtector.KeyScope, (byte[])sealedEntry!, cancellationToken);
            return JsonSerializer.Deserialize<CachedRecipientSnapshot>(plaintext);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.SnapshotCacheUnavailable(recipientId, exception);
            return null;
        }
    }

    /// <summary>Stores a fresh entry; called after a successful store read.</summary>
    public async Task StoreAsync(RecipientSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = new CachedRecipientSnapshot(snapshot, timeProvider.GetUtcNow(), Stale: false);
        try
        {
            var sealedEntry = await cipher.EncryptAsync(
                ContactValueProtector.KeyScope,
                JsonSerializer.SerializeToUtf8Bytes(entry),
                cancellationToken);
            await redis.Database.StringSetAsync(Key(snapshot.RecipientId), sealedEntry, redis.Ttl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.SnapshotCacheUnavailable(
                snapshot.RecipientId, exception);
        }
    }

    /// <summary>
    /// Marks the entry stale, keeping it as the last known value under the
    /// remaining lifetime. Marking an absent entry is a no-op; failures fail
    /// open because the next fresh read repairs the entry anyway.
    /// </summary>
    public async Task MarkStaleAsync(string recipientId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RedisKey key = Key(recipientId);
            RedisValue sealedEntry = await redis.Database.StringGetAsync(key);
            if (sealedEntry.IsNullOrEmpty)
            {
                return;
            }

            var plaintext = await cipher.DecryptAsync(
                ContactValueProtector.KeyScope, (byte[])sealedEntry!, cancellationToken);
            CachedRecipientSnapshot? entry = JsonSerializer.Deserialize<CachedRecipientSnapshot>(plaintext);
            if (entry is null || entry.Stale)
            {
                return;
            }

            TimeSpan? remaining = await redis.Database.KeyTimeToLiveAsync(key);
            var resealed = await cipher.EncryptAsync(
                ContactValueProtector.KeyScope,
                JsonSerializer.SerializeToUtf8Bytes(entry with { Stale = true }),
                cancellationToken);
            await redis.Database.StringSetAsync(key, resealed, remaining ?? redis.Ttl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.SnapshotCacheUnavailable(recipientId, exception);
        }
    }

    private RedisKey Key(string recipientId) => $"{redis.KeyPrefix}recipient:{recipientId}";
}
