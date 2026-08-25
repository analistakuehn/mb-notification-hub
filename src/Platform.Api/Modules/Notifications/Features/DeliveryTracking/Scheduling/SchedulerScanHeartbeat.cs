namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// What the health check of this role reads: when a round of the scheduler
/// last finished, and what the last one that failed complained about.
/// <para>
/// A singleton and monotonic on purpose. The failure mode this role has to
/// make visible is silence: a scheduler that stopped raises nothing, throws
/// nothing and drains no queue, it simply stops rescuing deliveries, and the
/// only way to notice from outside is that rounds stopped landing. Measuring
/// the gap in elapsed monotonic time rather than in wall-clock instants keeps
/// a clock adjustment from reading as a stalled scheduler or, worse, from
/// hiding one.
/// </para>
/// </summary>
internal sealed class SchedulerScanHeartbeat(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private long? _lastRoundTimestamp;
    private string? _lastFailure;

    /// <summary>Records a round that finished, whatever it found.</summary>
    internal void RoundCompleted()
    {
        lock (_gate)
        {
            _lastRoundTimestamp = timeProvider.GetTimestamp();
            _lastFailure = null;
        }
    }

    /// <summary>Records a round that threw, keeping the reason for the health report.</summary>
    internal void RoundFailed(string reason)
    {
        lock (_gate)
        {
            _lastFailure = reason;
        }
    }

    /// <summary>
    /// How long since the last completed round, and the reason the last
    /// failure gave. A null age means no round has ever completed, which is
    /// what a host that just started reports and what a host whose very first
    /// round failed keeps reporting.
    /// </summary>
    internal (TimeSpan? SinceLastRound, string? LastFailure) Read()
    {
        lock (_gate)
        {
            TimeSpan? since = _lastRoundTimestamp is { } timestamp
                ? timeProvider.GetElapsedTime(timestamp)
                : null;
            return (since, _lastFailure);
        }
    }
}
