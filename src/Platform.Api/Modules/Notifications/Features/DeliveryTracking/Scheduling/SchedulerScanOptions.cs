using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Configuration of the database-backed scheduler. Every knob here is a
/// deployment decision rather than a contract: the scans hold no state of
/// their own, so changing an interval or a batch size changes how often and
/// how much work is claimed, never what the work means.
/// </summary>
public sealed class SchedulerScanOptions
{
    public const string SectionName = "Modules:Notifications:SchedulerScan";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Pause between rounds. It adds at most its own length to a fallback
    /// deadline, so it is not a free knob: it is a term of the arithmetic that
    /// has to fit inside the accepted time to a fallback SMS.
    /// <para>
    /// The sum the default is derived from is the thirty second deadline of the
    /// first critical step, plus one interval, plus the outbox and relay hops,
    /// plus the Core stage, plus the provider call. At two seconds that sum
    /// stays inside the accepted window with the provider timeout counted in
    /// full; at five it did not, and nothing measured it, because no oracle
    /// asserts elapsed time on this path.
    /// </para>
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many rows one scan claims per round. It bounds the transaction, not
    /// the backlog: a round that fills its batch simply leaves the rest for the
    /// next one, and the ordering makes the oldest work go first.
    /// </summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 200;

    /// <summary>
    /// How long an attempt may sit on an inconclusive verdict before the
    /// scheduler asks for the next plan step. Only critical and authentication
    /// flows are asked for at all, because on every other class an unresolved
    /// send is cheaper to reconcile than to duplicate.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan UnknownGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a written fallback trigger is trusted to still be in flight.
    /// Past it, the request is cleared and the attempt returns to the scan.
    /// <para>
    /// The window is what keeps a trigger that never reached its handler from
    /// parking a plan step forever, and it is safe to make it short only
    /// because unicity is decided by the claim in the handler and never by the
    /// number of asks. Too short costs redundant queue rows the handler
    /// answers as duplicates; too long costs delay on the rare message that
    /// was lost.
    /// </para>
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "1.00:00:00")]
    public TimeSpan FallbackRequestRetry { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many consecutive intervals may pass with no completed round before
    /// the role reports itself unhealthy. A stopped scheduler is silent by
    /// nature: nothing errors, deliveries simply stop being rescued.
    /// </summary>
    [Range(2, 100)]
    public int HealthyRoundsMissedLimit { get; init; } = 6;
}
