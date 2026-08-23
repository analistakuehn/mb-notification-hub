using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;

/// <summary>
/// Lazy multiplexer of the ingestion control plane. This module never resolves
/// the bare <see cref="IConnectionMultiplexer"/> from the container, because
/// another module registers its own; the dedicated wrapper keeps the two
/// connections independent. Connection failures surface as exceptions on the
/// first operation, never at host start: <c>AbortOnConnectFail</c> is forced
/// off so the callers' fail-open handling owns every failure path.
/// </summary>
internal sealed class NotificationsRedisConnection(IOptions<NotificationsRedisOptions> options) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection = new(() =>
    {
        ConfigurationOptions configuration = ConfigurationOptions.Parse(options.Value.ConnectionString);
        configuration.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(configuration);
    });

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
