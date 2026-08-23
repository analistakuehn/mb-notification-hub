using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// Configuration of the periodic chain verification: the sensor that turns
/// "the trail is tamper-evident" into "tampering is noticed within the hour".
/// The stabilization watermark keeps the job away from rows whose transaction
/// may still be in flight; the full-replay cadence bounds how long a defect
/// before the last checkpoint could stay unnoticed.
/// </summary>
public sealed class ChainVerificationOptions
{
    public const string SectionName = "Modules:Audit:ChainVerification";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Rows more recent than this are left to the next round. Sequence values
    /// are drawn before commit, so a very recent range can still be filling
    /// in; verifying it would raise alarms about nothing.
    /// </summary>
    public TimeSpan StabilizationWatermark { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often an open partition is replayed whole from its anchor instead
    /// of resuming at the checkpoint. Incremental rounds only cover what came
    /// after the checkpoint; the full replay is what re-examines the history
    /// behind it.
    /// </summary>
    public TimeSpan FullVerificationInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Rows read per batch; a round keeps reading until the range ends.</summary>
    [Range(100, 1_000_000)]
    public int BatchSize { get; init; } = 20_000;
}
