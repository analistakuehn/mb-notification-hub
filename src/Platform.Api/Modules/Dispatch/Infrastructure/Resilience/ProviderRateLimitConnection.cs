using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>
/// Lazy multiplexer of the per-provider rate limit. This module never resolves
/// the bare <see cref="IConnectionMultiplexer"/> from the container, because
/// sibling modules register their own; the dedicated wrapper keeps the
/// connections independent. Connection failures surface as exceptions on the
/// first operation, never at host start: <c>AbortOnConnectFail</c> is forced
/// off so the limiter's fail-open handling owns every failure path.
/// </summary>
internal sealed class ProviderRateLimitConnection(IOptions<ProviderRateLimitOptions> options) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection = new(() =>
    {
        ConfigurationOptions configuration = ConfigurationOptions.Parse(
            options.Value.RedisConnectionString);
        configuration.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(configuration);
    });

    /// <summary>Whether this deployment pointed the limiter at a store at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.RedisConnectionString);

    public string KeyPrefix => options.Value.KeyPrefix;

    public IDatabase Database => _connection.Value.GetDatabase();

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }
}
