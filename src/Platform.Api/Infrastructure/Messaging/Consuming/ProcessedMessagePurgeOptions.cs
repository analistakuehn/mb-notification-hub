using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Configuration of the processed-messages purge job. The retention sits
/// above the queue retention on purpose: a mark only becomes removable once
/// the broker can no longer redeliver the message it dedupes.
/// </summary>
public sealed class ProcessedMessagePurgeOptions
{
    public const string SectionName = "Platform:Messaging:ProcessedMessagePurge";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between purge rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Age past which a mark is removed; above the 14-day SQS retention.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "60.00:00:00")]
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(15);
}
