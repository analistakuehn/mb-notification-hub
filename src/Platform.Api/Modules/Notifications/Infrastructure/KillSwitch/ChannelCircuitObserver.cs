using System.Collections.Concurrent;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

/// <summary>What one send verdict says about the provider circuit of a channel.</summary>
internal enum ChannelCircuitSignal
{
    /// <summary>The verdict says nothing about the circuit and changes no streak.</summary>
    None = 0,

    /// <summary>The pipeline refused the call because the circuit is open.</summary>
    CircuitOpen = 1,

    /// <summary>The call went through the pipeline, so the circuit was closed.</summary>
    ProviderAnswered = 2,
}

/// <summary>
/// How long the provider circuit of one channel has been open, as this process
/// saw it. The state is deliberately in memory and per process: the circuit
/// breaker it observes is itself per process, and a shared window would claim
/// an agreement between instances that does not exist. The consequence is
/// written where the observation is acted on, not here.
/// </summary>
internal sealed class ChannelCircuitObserver(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, OpenStreak> _streaks = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one open-circuit observation and answers whether this call is
    /// the one that crossed the window. It answers true at most once per
    /// streak: the consequence is a global stop, and repeating it on every
    /// message would hammer the store for a decision already taken.
    /// </summary>
    internal bool ObserveOpenCircuit(string channel, TimeSpan window)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        OpenStreak streak = _streaks.GetOrAdd(channel, _ => new OpenStreak(now));
        if (streak.Reported || now - streak.OpenSince <= window) return false;

        // Only the caller that swaps the streak for its reported twin owns the
        // crossing: two messages of the same channel can observe the same
        // window at once, and the stop must be decided once.
        return _streaks.TryUpdate(channel, new OpenStreak(streak.OpenSince) { Reported = true }, streak);
    }

    /// <summary>
    /// Records that the pipeline let a call through, which is the only proof
    /// that the circuit is closed, and ends any streak of this channel.
    /// </summary>
    internal void ObserveProviderAnswered(string channel) => _streaks.TryRemove(channel, out _);

    /// <summary>
    /// A run of open-circuit observations. A class, not a record: the update
    /// above is a compare-and-swap over the instance, and value equality would
    /// let two callers swap the same streak.
    /// </summary>
    private sealed class OpenStreak(DateTimeOffset openSince)
    {
        internal DateTimeOffset OpenSince { get; } = openSince;

        internal bool Reported { get; init; }
    }
}
