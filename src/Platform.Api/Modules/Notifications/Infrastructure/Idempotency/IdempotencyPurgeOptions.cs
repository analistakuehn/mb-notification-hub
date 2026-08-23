using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

/// <summary>
/// Configuration of the idempotency purge job. The retention window is part
/// of the idempotency contract: a replay beyond it finds no registration and
/// creates a new notification on purpose.
/// </summary>
public sealed class IdempotencyPurgeOptions
{
    public const string SectionName = "Modules:Notifications:IdempotencyPurge";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between purge rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Age past which a registration is removed.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
}
