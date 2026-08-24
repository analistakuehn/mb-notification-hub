using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal interface IKillSwitchSnapshotSource
{
    Task<IReadOnlySet<KillSwitchAddress>> LoadActiveAsync(CancellationToken cancellationToken);
}

internal sealed class KillSwitchCache(
    IKillSwitchSnapshotSource source,
    TimeProvider timeProvider,
    ILogger<KillSwitchCache>? logger = null) : IKillSwitch, IDisposable
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CacheSnapshot _snapshot = CacheSnapshot.Cold;
    private long _generation;

    public async ValueTask<KillSwitchEvaluation> EvaluateAsync(
        KillSwitchScope scope,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var address = new KillSwitchAddress(scope, key.Trim());
        CacheSnapshot current = await GetSnapshotAsync(cancellationToken);
        return current.Evaluate(address);
    }

    internal async ValueTask<KillSwitchSnapshotStatus> EnsureAvailableAsync(
        CancellationToken cancellationToken)
    {
        CacheSnapshot current = await GetSnapshotAsync(cancellationToken);
        return current.Status(timeProvider, timeProvider.GetTimestamp());
    }

    private async ValueTask<CacheSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetTimestamp();
        CacheSnapshot current = Volatile.Read(ref _snapshot);
        if (current.CanServe(timeProvider, timestamp))
        {
            return current;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                timestamp = timeProvider.GetTimestamp();
                current = Volatile.Read(ref _snapshot);
                if (current.CanServe(timeProvider, timestamp))
                {
                    return current;
                }

                var generation = Volatile.Read(ref _generation);
                var loadStartedTimestamp = timeProvider.GetTimestamp();
                DateTimeOffset loadStartedAt = timeProvider.GetUtcNow();
                try
                {
                    IReadOnlySet<KillSwitchAddress> loaded =
                        await source.LoadActiveAsync(cancellationToken);
                    if (generation != Volatile.Read(ref _generation))
                    {
                        continue;
                    }

                    var loadCompletedTimestamp = timeProvider.GetTimestamp();
                    if (timeProvider.GetElapsedTime(
                            loadStartedTimestamp,
                            loadCompletedTimestamp) >= SnapshotTtl)
                    {
                        CacheSnapshot unavailable = CacheSnapshot.Unavailable(
                            loadCompletedTimestamp,
                            timeProvider.GetUtcNow().Add(RefreshFailureBackoff));
                        Interlocked.Exchange(ref _snapshot, unavailable);
                        return unavailable;
                    }

                    var fresh = CacheSnapshot.Fresh(
                        loaded,
                        loadStartedTimestamp,
                        loadStartedAt.Add(SnapshotTtl));
                    Interlocked.Exchange(ref _snapshot, fresh);
                    return fresh;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger?.KillSwitchRefreshFailed(exception);
                    if (generation != Volatile.Read(ref _generation))
                    {
                        continue;
                    }

                    var failureTimestamp = timeProvider.GetTimestamp();
                    var unavailable = CacheSnapshot.Unavailable(
                        failureTimestamp,
                        timeProvider.GetUtcNow().Add(RefreshFailureBackoff));
                    Interlocked.Exchange(ref _snapshot, unavailable);
                    return unavailable;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _snapshot, CacheSnapshot.Cold);
    }

    internal KillSwitchSnapshotStatus Status()
    {
        CacheSnapshot current = Volatile.Read(ref _snapshot);
        return current.Status(timeProvider, timeProvider.GetTimestamp());
    }

    public void Dispose() => _refreshGate.Dispose();

    private sealed record CacheSnapshot(
        KillSwitchSnapshotState State,
        HashSet<KillSwitchAddress> Active,
        long? LoadedTimestamp,
        DateTimeOffset? ExpiresAt)
    {
        internal static CacheSnapshot Cold { get; } = new(
            KillSwitchSnapshotState.Cold,
            [],
            LoadedTimestamp: null,
            ExpiresAt: null);

        internal static CacheSnapshot Fresh(
            IReadOnlySet<KillSwitchAddress> active,
            long loadedTimestamp,
            DateTimeOffset expiresAt)
            => new(KillSwitchSnapshotState.Fresh, [.. active], loadedTimestamp, expiresAt);

        internal static CacheSnapshot Unavailable(
            long loadedTimestamp,
            DateTimeOffset expiresAt)
            => new(KillSwitchSnapshotState.Unavailable, [], loadedTimestamp, expiresAt);

        internal bool CanServe(TimeProvider provider, long timestamp)
        {
            TimeSpan serviceWindow = State switch
            {
                KillSwitchSnapshotState.Fresh => SnapshotTtl,
                KillSwitchSnapshotState.Unavailable => RefreshFailureBackoff,
                _ => TimeSpan.Zero,
            };
            return LoadedTimestamp is { } loadedTimestamp
                && provider.GetElapsedTime(loadedTimestamp, timestamp) < serviceWindow;
        }

        internal KillSwitchEvaluation Evaluate(KillSwitchAddress address)
            => State == KillSwitchSnapshotState.Unavailable
                ? KillSwitchEvaluation.Unavailable
                : Active.Contains(address)
                    ? KillSwitchEvaluation.Blocked
                    : KillSwitchEvaluation.Allowed;

        internal KillSwitchSnapshotStatus Status(TimeProvider provider, long timestamp)
        {
            KillSwitchSnapshotState reported = State switch
            {
                KillSwitchSnapshotState.Unavailable => KillSwitchSnapshotState.Unavailable,
                KillSwitchSnapshotState.Fresh when !CanServe(provider, timestamp) =>
                    KillSwitchSnapshotState.Expired,
                _ => State,
            };
            return new KillSwitchSnapshotStatus(reported, ExpiresAt);
        }
    }
}

internal enum KillSwitchSnapshotState
{
    Cold = 0,
    Fresh = 1,
    Expired = 2,
    Unavailable = 3,
}

internal sealed record KillSwitchSnapshotStatus(
    KillSwitchSnapshotState State,
    DateTimeOffset? ExpiresAt);
