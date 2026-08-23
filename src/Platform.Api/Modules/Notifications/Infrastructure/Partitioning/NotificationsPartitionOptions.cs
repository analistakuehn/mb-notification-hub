using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;

/// <summary>
/// Configuration of the notification partition provisioning. Defaults keep
/// the job active with a daily cadence and a two-month creation horizon.
/// Provisioning is the only step this module ever runs: closing steps such as
/// write revokes belong exclusively to the audit trail's semantics and never
/// apply to notification partitions.
/// </summary>
public sealed class NotificationsPartitionOptions
{
    public const string SectionName = "Modules:Notifications:PartitionManager";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between provisioning rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How many months ahead of the current month must already have a partition.</summary>
    [Range(1, 12)]
    public int MonthsAhead { get; init; } = 2;

    /// <summary>
    /// Minimum days of contiguous future partition coverage; below it the
    /// module health check reports degradation.
    /// </summary>
    [Range(1, 365)]
    public int FutureWindowMinimumDays { get; init; } = 21;
}
