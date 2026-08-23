using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

/// <summary>
/// Configuration of the partition-manager job. Defaults keep the job active
/// with a daily cadence and a two-month creation horizon over the module's
/// partitioned tables. The revoke and retention steps ship disabled: they
/// depend on database roles and on the WORM bucket delivered by a later phase.
/// </summary>
public sealed partial class PartitionManagerOptions
{
    public const string SectionName = "Modules:TemplateManagement:PartitionManager";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between maintenance rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>How many months ahead of the current month must already have a partition.</summary>
    [Range(1, 12)]
    public int MonthsAhead { get; init; } = 2;

    /// <summary>
    /// Monthly-partitioned tables of this module's schema that the job keeps
    /// provisioned. Empty means the module default; a configured list replaces
    /// the default entirely, because configuration binding appends to any
    /// non-empty default and would make entries impossible to remove.
    /// </summary>
    public IReadOnlyList<string> PartitionedTables { get; init; } = [];

    /// <summary>
    /// Minimum days of contiguous future partition coverage; below it the
    /// module health check reports degradation.
    /// </summary>
    [Range(1, 365)]
    public int FutureWindowMinimumDays { get; init; } = 21;

    /// <summary>
    /// Gate for the write REVOKE on closed monthly partitions. Off by default:
    /// the step needs the dedicated database roles, not provisioned yet, and
    /// the job only reports the gate state while the step does not exist.
    /// </summary>
    public bool EnableRevokeOnClosedPartitions { get; init; }

    /// <summary>
    /// Gate for the retention cycle (DETACH, WORM export, drop). Off by
    /// default: the step needs the WORM bucket, not provisioned yet, and the
    /// job only reports the gate state while the step does not exist.
    /// </summary>
    public bool EnableRetentionCycle { get; init; }

    /// <summary>
    /// A table name this job accepts: an unquoted lowercase PostgreSQL
    /// identifier. Anything else is rejected before reaching the DDL.
    /// </summary>
    public static bool IsSafeTableName(string? value)
        => value is not null && SafeTableName().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex SafeTableName();
}
