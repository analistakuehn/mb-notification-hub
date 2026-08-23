using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Domain;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

/// <summary>
/// Serves provider resolution from a short-lived snapshot of the
/// materialized configuration table. A stale snapshot triggers one refresh
/// (single flight); when the refresh fails and an older snapshot exists, the
/// older snapshot keeps serving, because delivery must not stop while the
/// configuration table is briefly unreachable.
/// </summary>
internal sealed class CachedProviderConfigStore(
    IServiceScopeFactory scopeFactory,
    IOptions<ProviderConfigOptions> options,
    TimeProvider timeProvider,
    ILogger<CachedProviderConfigStore> logger) : IProviderConfigStore, IDisposable
{
    private readonly TimeSpan _timeToLive = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot? _snapshot;

    public async Task<Result<string>> ResolveProviderKeyAsync(
        Channel channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        Snapshot? current = Volatile.Read(ref _snapshot);
        if (current is null || IsStale(current))
        {
            current = await RefreshAsync(cancellationToken);
        }

        if (current is null)
        {
            return Result.IntegrationFailure<string>(
                "Provider configuration is unavailable and no previous snapshot exists.");
        }

        return current.ProviderKeyByChannel.TryGetValue(channel.Value, out var providerKey)
            ? Result.Success(providerKey)
            : Result.IntegrationFailure<string>(
                $"No provider is configured for channel '{channel.Value}'.");
    }

    public void Dispose() => _refreshGate.Dispose();

    private async Task<Snapshot?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            Snapshot? current = Volatile.Read(ref _snapshot);
            if (current is not null && !IsStale(current))
            {
                return current;
            }

            try
            {
                Snapshot refreshed = await LoadAsync(cancellationToken);
                Volatile.Write(ref _snapshot, refreshed);
                var channelCount = refreshed.ProviderKeyByChannel.Count;
                logger.ProviderConfigRefreshed(channelCount);
                return refreshed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (current is null)
                {
                    logger.ProviderConfigUnavailable(exception);
                }
                else
                {
                    logger.ProviderConfigRefreshFailed(exception);
                }

                return current;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        DispatchDbContext dbContext = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        List<ProviderSelection> rows = await dbContext.ProviderSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Dictionary<string, string> providerKeyByChannel = rows
            .GroupBy(row => row.ChannelValue, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(row => row.Priority)
                    .ThenBy(row => row.ProviderKey, StringComparer.Ordinal)
                    .First().ProviderKey,
                StringComparer.Ordinal);

        return new Snapshot(providerKeyByChannel, timeProvider.GetUtcNow());
    }

    private bool IsStale(Snapshot candidate)
        => timeProvider.GetUtcNow() - candidate.LoadedAt >= _timeToLive;

    private sealed record Snapshot(
        IReadOnlyDictionary<string, string> ProviderKeyByChannel,
        DateTimeOffset LoadedAt);
}
