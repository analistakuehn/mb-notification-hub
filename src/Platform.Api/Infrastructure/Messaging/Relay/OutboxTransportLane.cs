namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Transport lanes the relay drains inside every band. The order is fixed:
/// the internal queues carry the delivery path and drain before the corporate
/// bus, which carries integration events. A lane is the failure unit of a
/// pass, so an unavailable transport never holds back the other one.
/// </summary>
internal static class OutboxTransportLanes
{
    /// <summary>Every lane, in the order a full instance drains them.</summary>
    internal static readonly string[] DrainOrder =
    [
        OutboxTransports.Sqs,
        OutboxTransports.Kafka,
    ];

    /// <summary>
    /// The drain order restricted to the configured transports and to the
    /// lanes this instance actually has a publisher for. The configured order
    /// never reorders the drain, only selects from it; an empty configuration
    /// selects every registered lane.
    /// </summary>
    internal static string[] Restrict(
        IReadOnlyCollection<string> configured,
        IReadOnlyCollection<string> registered)
    {
        var available = new HashSet<string>(registered, StringComparer.Ordinal);
        if (configured.Count > 0)
        {
            available.IntersectWith(configured);
        }

        return [.. DrainOrder.Where(available.Contains)];
    }
}
