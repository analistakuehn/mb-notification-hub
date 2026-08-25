using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// Configuration of the monthly evidence report: when a closed month becomes
/// reportable, how often the job looks, and how far back it keeps looking.
/// Nothing here changes what the report says; it changes when the report is
/// allowed to say it.
/// </summary>
public sealed class MonthlyEvidenceReportOptions
{
    public const string SectionName = "Modules:Compliance:MonthlyEvidenceReport";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Pause between rounds; the first round runs at host start. A day,
    /// because a round is not cheap and does not need to be frequent: it
    /// recomputes the whole report of every month inside the lookback, and the
    /// aggregates behind it group over a monthly partition of the trail. The
    /// month it publishes already waited out the grace, so a faster cadence
    /// buys hours on a report nobody reads before the monthly review.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long after the end of a month the report may be composed. The
    /// delivery figures move backwards in time: the rear-guard reconciliation
    /// runs daily and corrects, today, an attempt sent yesterday whose provider
    /// never reported. A report closed on the first of the month would archive
    /// as unknown what the next round resolves, and the archive is immutable,
    /// so the correction would never reach it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "31.00:00:00")]
    public TimeSpan ReconciliationGrace { get; init; } = TimeSpan.FromDays(3);

    /// <summary>
    /// How many closed months a round revisits. A revisit is what makes the
    /// job self-healing after an outage; the bound is what keeps a month whose
    /// sources moved after it was archived from being reported as a finding
    /// forever.
    /// </summary>
    [Range(1, 12)]
    public int LookbackMonths { get; init; } = 2;
}
