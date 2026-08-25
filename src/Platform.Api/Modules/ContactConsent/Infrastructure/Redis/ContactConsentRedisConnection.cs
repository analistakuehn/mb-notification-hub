using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Redis;

/// <summary>
/// Lazy multiplexer of the snapshot cache. This module never resolves the
/// bare <see cref="IConnectionMultiplexer"/> from the container, because
/// other modules register their own; the dedicated wrapper keeps the
/// connections independent. Connection failures surface as exceptions on the
/// first operation, never at host start: <c>AbortOnConnectFail</c> is forced
/// off so the cache's fail-open handling owns every failure path.
/// </summary>
internal sealed class ContactConsentRedisConnection(
    IOptions<ContactConsentRedisOptions> options) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection = new(() =>
    {
        var configuration = ConfigurationOptions.Parse(options.Value.ConnectionString);
        configuration.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(configuration);
    });

    public string KeyPrefix => options.Value.KeyPrefix;

    public TimeSpan Ttl => options.Value.Ttl;

    public IDatabase Database => _connection.Value.GetDatabase();

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }
}
