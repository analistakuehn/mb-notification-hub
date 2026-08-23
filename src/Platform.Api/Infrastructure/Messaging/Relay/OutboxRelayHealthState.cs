using System.Collections.Concurrent;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// In-process record of destinations whose queue does not exist, fed by the
/// publisher and read by the relay health check. A missing queue is an
/// infrastructure failure: the health check degrades while the rows wait, and
/// the entry clears as soon as the destination resolves again.
/// </summary>
internal sealed class OutboxRelayHealthState
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingQueues = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, DateTimeOffset> MissingQueues => _missingQueues;

    public void ReportQueueMissing(string destination, DateTimeOffset observedAt)
        => _missingQueues[destination] = observedAt;

    public void ReportQueueAvailable(string destination)
        => _missingQueues.TryRemove(destination, out _);
}
