using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

/// <summary>
/// Tuning of the asynchronous half of delivery tracking. The defaults assume
/// the ordinary race this design accepts: a provider may call back before the
/// transaction that recorded the send has committed, so feedback whose attempt
/// is not visible yet waits a little and comes back, instead of failing.
/// </summary>
public sealed class DeliveryTrackingOptions
{
    public const string SectionName = "Modules:Notifications:DeliveryTracking";

    /// <summary>
    /// How long feedback keeps coming back looking for its attempt. Past this
    /// age the message is discarded with a record: the send transaction is
    /// long committed or was rolled back, and returning forever would keep one
    /// unmatchable message circulating for the retention of the queue.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan UnresolvedAttemptWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Pause before feedback whose attempt is not visible yet comes back.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan UnresolvedAttemptRetryDelay { get; init; } = TimeSpan.FromSeconds(20);
}
