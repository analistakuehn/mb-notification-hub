using NotificationHub.Api.Modules.Compliance.Features.Reporting;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// When a closed month becomes reportable. The archive cannot be rewritten, so
/// the wait is the only chance the report has of carrying the corrections the
/// rear-guard reconciliation makes to a month that already ended.
/// </summary>
public sealed class MonthlyEvidenceScheduleTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(3);

    [Fact]
    public void A_closed_month_waits_out_the_reconciliation_grace()
    {
        var justClosed = new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.Zero);

        MonthlyEvidenceReportService.DueMonths(justClosed, Grace, lookbackMonths: 2)
            .ShouldNotContain(new ReportMonth(2026, 7));
    }

    [Fact]
    public void The_month_becomes_reportable_once_the_grace_has_elapsed()
    {
        var afterGrace = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

        MonthlyEvidenceReportService.DueMonths(afterGrace, Grace, lookbackMonths: 2)
            .ShouldContain(new ReportMonth(2026, 7));
    }

    [Fact]
    public void The_month_still_being_written_is_never_a_candidate()
    {
        var midMonth = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

        MonthlyEvidenceReportService.DueMonths(midMonth, Grace, lookbackMonths: 6)
            .ShouldNotContain(new ReportMonth(2026, 8));
    }

    [Fact]
    public void A_round_revisits_the_closed_months_inside_the_lookback_most_recent_first()
    {
        // Revisiting is what makes the job self-healing after an outage: the
        // months it missed are still addressed by the same deterministic keys.
        var afterGrace = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        MonthlyEvidenceReportService.DueMonths(afterGrace, Grace, lookbackMonths: 3)
            .ShouldBe([new ReportMonth(2026, 7), new ReportMonth(2026, 6), new ReportMonth(2026, 5)]);
    }

    [Fact]
    public void A_month_that_ends_a_year_rolls_over_to_the_previous_one()
    {
        var january = new DateTimeOffset(2027, 1, 10, 0, 0, 0, TimeSpan.Zero);

        MonthlyEvidenceReportService.DueMonths(january, Grace, lookbackMonths: 1)
            .ShouldBe([new ReportMonth(2026, 12)]);
    }
}
