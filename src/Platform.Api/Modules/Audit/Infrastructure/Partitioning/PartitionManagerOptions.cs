using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Infrastructure.Partitioning;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// Configuration of the partition-manager job. Defaults keep the job active
/// with a daily cadence and a two-month creation horizon over the module's
/// partitioned tables. The revoke and retention steps ship disabled: they
/// depend on database roles and on the WORM bucket delivered by a later phase.
/// </summary>
public sealed class PartitionManagerOptions
{
    public const string SectionName = "Modules:Audit:PartitionManager";

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
    /// Gate for the write REVOKE (and the closed-write trigger) on closed
    /// monthly partitions. Off by default: it depends on the appender role
    /// existing in the target database, and without it the closing cycle
    /// refuses to proceed, because a partition that can still receive rows
    /// must not be declared final.
    /// </summary>
    public bool EnableRevokeOnClosedPartitions { get; init; }

    /// <summary>
    /// Gate for the closing cycle up to and including DETACH. Off by default:
    /// it depends on the WORM bucket being provisioned. It never authorizes
    /// destroying anything; that is a separate gate on purpose.
    /// </summary>
    public bool EnableRetentionCycle { get; init; }

    /// <summary>
    /// Gate for destroying a detached partition. Deliberately separate from
    /// every other gate and off by default: exporting evidence is additive,
    /// dropping a table is not, and the two must never share one switch.
    /// </summary>
    public bool EnableDropDetachedPartitions { get; init; }

    /// <summary>
    /// Days after the end of a month before its partition is closed. It gives
    /// slow effects time to land, so a partition is declared final only once
    /// nothing else is expected in it.
    /// </summary>
    [Range(0, 365)]
    public int ClosingGraceDays { get; init; } = 2;

    /// <summary>
    /// How long a detached partition stays queryable in the database before
    /// the drop gate may destroy it. The evidence lives in the immutable store
    /// from the closing export onwards; this window is about operational
    /// convenience, not about proof.
    /// </summary>
    [Range(1, 3650)]
    public int DatabaseResidencyDays { get; init; } = 90;

    /// <summary>
    /// A table name this job accepts: the platform's safe-identifier rule for
    /// unquoted lowercase PostgreSQL identifiers. Anything else is rejected
    /// before reaching the DDL.
    /// </summary>
    public static bool IsSafeTableName(string? value)
        => PartitionIdentifiers.IsSafeIdentifier(value);
}
