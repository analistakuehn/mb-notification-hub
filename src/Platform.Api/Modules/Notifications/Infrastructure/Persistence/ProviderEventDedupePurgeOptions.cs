using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Configuration of the provider deduplication purge. The retention is what
/// bounds the ledger: a provider redelivers a callback for as long as its own
/// retry policy lasts, and a mark only becomes removable once no redelivery of
/// the event it names can still arrive.
/// </summary>
public sealed class ProviderEventDedupePurgeOptions
{
    public const string SectionName = "Modules:Notifications:ProviderEventDedupePurge";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between purge rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Age past which a deduplication mark is removed.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);
}
