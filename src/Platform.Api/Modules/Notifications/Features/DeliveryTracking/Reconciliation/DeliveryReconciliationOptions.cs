using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>
/// Configuration of the rear-guard reconciliation. Every knob is a deployment
/// decision: changing one changes how often the hub asks providers about the
/// sends they never reported on, and how much it asks about per round, never
/// what an answer means.
/// </summary>
public sealed class DeliveryReconciliationOptions
{
    public const string SectionName = "Modules:Notifications:DeliveryReconciliation";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Pause between rounds. A day, because this is the correction of last
    /// resort and not a delivery path: the fallback already ran, the deadline
    /// already elapsed, and what is left is an attempt whose record is wrong
    /// rather than a message somebody is waiting for.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long an attempt may sit with no provider answer before this hub
    /// goes and asks. It is long on purpose: providers report asynchronously
    /// and a hub that asked earlier would mostly be paying for answers that
    /// were already on their way.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How many attempts one round asks about. It bounds the round, not the
    /// backlog: what a round leaves behind is picked up by the next one, and
    /// the oldest attempts go first.
    /// </summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 200;

    /// <summary>
    /// How many parked attempts of concluded notifications one round retires
    /// from the scheduler's indexes. Bounded for the same reason the lookup
    /// batch is: the first round after this job is deployed meets every row
    /// ever left behind, and a single statement over all of them would hold
    /// its locks for as long as that takes.
    /// </summary>
    [Range(1, 100_000)]
    public int LiabilityBatchSize { get; init; } = 2_000;
}
