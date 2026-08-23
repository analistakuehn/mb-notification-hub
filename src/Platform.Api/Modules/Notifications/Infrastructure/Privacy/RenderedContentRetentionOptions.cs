using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// Configuration of the rear-guard sweep over rendered content. An attempt
/// that never reached a terminal verdict keeps the complete form nobody will
/// read again, so the sweep discards it once the notification's own validity
/// is over. The window is the notification TTL plus this grace: the TTL is
/// already stamped per row as the expiry instant, and the grace covers the
/// latency between the expiry and the last legitimate send.
/// </summary>
public sealed class RenderedContentRetentionOptions
{
    public const string SectionName = "Modules:Notifications:RenderedContentRetention";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between sweep rounds; the first round runs at host start.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wait after the notification expiry before an abandoned attempt is
    /// masked. Long enough that a message still in flight sends the content it
    /// was queued with, short enough that abandoned content does not survive
    /// the day.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan Grace { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How many attempts one round settles, so a backlog drains over several rounds.</summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 500;
}
