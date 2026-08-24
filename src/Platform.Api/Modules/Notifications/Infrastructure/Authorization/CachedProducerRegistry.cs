using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// Serves producer authorization from a short-lived snapshot of the
/// materialized registry table, mirroring the provider-configuration reader of
/// the dispatch side. A stale snapshot triggers one refresh (single flight);
/// no snapshot can serve after reaching sixty seconds of absolute age. A
/// configured cache TTL may trigger refresh sooner, but never later. If that
/// mandatory refresh fails, the registry is unavailable and the consumer gate
/// closes.
/// </summary>
internal sealed class CachedProducerRegistry(
    IServiceScopeFactory scopeFactory,
    IOptions<ProducerRegistryOptions> options,
    TimeProvider timeProvider,
    ILogger<CachedProducerRegistry> logger) : IProducerRegistry, IDisposable
{
    private const int MaximumSnapshotAgeSeconds = 60;

    private static readonly TimeSpan MaximumSnapshotAge =
        TimeSpan.FromSeconds(MaximumSnapshotAgeSeconds);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _refreshAfter = TimeSpan.FromSeconds(
        Math.Min(options.Value.CacheTtlSeconds, MaximumSnapshotAgeSeconds));
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private RefreshFailure? _refreshFailure;
    private RegistrySnapshot? _snapshot;

    public async Task<ProducerGrants?> CurrentAsync(CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetTimestamp();
        RegistrySnapshot? current = Volatile.Read(ref _snapshot);
        if (current is not null && !RequiresRefresh(current, timestamp))
        {
            return current.Grants;
        }

        if (RefreshIsBackedOff(timestamp))
        {
            return current is not null && CanServe(current, timestamp)
                ? current.Grants
                : null;
        }

        return (await RefreshAsync(cancellationToken))?.Grants;
    }

    public void Dispose() => _refreshGate.Dispose();

    private async Task<RegistrySnapshot?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = timeProvider.GetTimestamp();
            RegistrySnapshot? current = Volatile.Read(ref _snapshot);
            if (current is not null && !RequiresRefresh(current, timestamp))
            {
                return current;
            }

            if (RefreshIsBackedOff(timestamp))
            {
                return current is not null && CanServe(current, timestamp)
                    ? current
                    : null;
            }

            try
            {
                RegistrySnapshot refreshed = await LoadAsync(cancellationToken);
                var loadCompletedTimestamp = timeProvider.GetTimestamp();
                if (!CanServe(refreshed, loadCompletedTimestamp))
                {
                    return HandleRefreshFailure(
                        current,
                        loadCompletedTimestamp,
                        new TimeoutException(
                            "A recarga do registro de produtores consumiu a idade máxima do snapshot."));
                }

                Volatile.Write(ref _snapshot, refreshed);
                Volatile.Write(ref _refreshFailure, null);
                logger.ProducerRegistryRefreshed(refreshed.Grants.Count);
                return refreshed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HandleRefreshFailure(
                    current,
                    timeProvider.GetTimestamp(),
                    exception);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<RegistrySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var loadedTimestamp = timeProvider.GetTimestamp();
        DateTimeOffset loadedAt = timeProvider.GetUtcNow();
        using IServiceScope scope = scopeFactory.CreateScope();
        NotificationsDbContext dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        List<ProducerGrant> rows = await dbContext.ProducerRegistrations
            .AsNoTracking()
            .Select(registration => new ProducerGrant(
                registration.Principal, registration.Application, registration.Class))
            .ToListAsync(cancellationToken);

        var grants = new ProducerGrants(
            new HashSet<ProducerGrant>(rows),
            loadedAt);
        return new RegistrySnapshot(grants, loadedTimestamp);
    }

    private RegistrySnapshot? HandleRefreshFailure(
        RegistrySnapshot? current,
        long failureTimestamp,
        Exception exception)
    {
        Volatile.Write(ref _refreshFailure, new RefreshFailure(failureTimestamp));
        var canServeCurrent = current is not null && CanServe(current, failureTimestamp);
        if (current is null)
        {
            logger.ProducerRegistryUnavailable(exception);
        }
        else
        {
            logger.ProducerRegistryRefreshFailed(exception);
        }

        return canServeCurrent ? current : null;
    }

    private bool RequiresRefresh(RegistrySnapshot candidate, long timestamp)
        => ElapsedSinceLoad(candidate, timestamp) >= _refreshAfter;

    private bool CanServe(RegistrySnapshot candidate, long timestamp)
        => ElapsedSinceLoad(candidate, timestamp) < MaximumSnapshotAge;

    private bool RefreshIsBackedOff(long timestamp)
    {
        RefreshFailure? failure = Volatile.Read(ref _refreshFailure);
        return failure is not null
            && timeProvider.GetElapsedTime(failure.Timestamp, timestamp) < RefreshFailureBackoff;
    }

    private TimeSpan ElapsedSinceLoad(RegistrySnapshot candidate, long timestamp)
        => timeProvider.GetElapsedTime(
            candidate.LoadedTimestamp,
            timestamp);

    private sealed record RegistrySnapshot(ProducerGrants Grants, long LoadedTimestamp);

    private sealed record RefreshFailure(long Timestamp);
}
