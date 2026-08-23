using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// Serves producer authorization from a short-lived snapshot of the
/// materialized registry table, mirroring the provider-configuration reader of
/// the dispatch side. A stale snapshot triggers one refresh (single flight);
/// when the refresh fails and an older snapshot exists, the older snapshot
/// keeps serving, because ingestion must not stop while the table is briefly
/// unreachable.
/// </summary>
internal sealed class CachedProducerRegistry(
    IServiceScopeFactory scopeFactory,
    IOptions<ProducerRegistryOptions> options,
    TimeProvider timeProvider,
    ILogger<CachedProducerRegistry> logger) : IProducerRegistry, IDisposable
{
    private readonly TimeSpan _timeToLive = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ProducerGrants? _snapshot;

    public async Task<ProducerGrants?> CurrentAsync(CancellationToken cancellationToken)
    {
        ProducerGrants? current = Volatile.Read(ref _snapshot);
        if (current is null || IsStale(current))
        {
            current = await RefreshAsync(cancellationToken);
        }

        return current;
    }

    public void Dispose() => _refreshGate.Dispose();

    private async Task<ProducerGrants?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ProducerGrants? current = Volatile.Read(ref _snapshot);
            if (current is not null && !IsStale(current))
            {
                return current;
            }

            try
            {
                ProducerGrants refreshed = await LoadAsync(cancellationToken);
                Volatile.Write(ref _snapshot, refreshed);
                logger.ProducerRegistryRefreshed(refreshed.Count);
                return refreshed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (current is null)
                {
                    logger.ProducerRegistryUnavailable(exception);
                }
                else
                {
                    logger.ProducerRegistryRefreshFailed(exception);
                }

                return current;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<ProducerGrants> LoadAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        NotificationsDbContext dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        List<ProducerGrant> rows = await dbContext.ProducerRegistrations
            .AsNoTracking()
            .Select(registration => new ProducerGrant(
                registration.Principal, registration.Application, registration.Class))
            .ToListAsync(cancellationToken);

        return new ProducerGrants(new HashSet<ProducerGrant>(rows), timeProvider.GetUtcNow());
    }

    private bool IsStale(ProducerGrants candidate)
        => timeProvider.GetUtcNow() - candidate.LoadedAt >= _timeToLive;
}
