using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

namespace NotificationHub.UnitTests.Audit;

public sealed class MonthlyPartitionsTests
{
    [Fact]
    public void Plans_the_reference_month_plus_the_requested_months_ahead_with_exclusive_upper_bounds()
    {
        IReadOnlyList<MonthlyPartitionWindow> windows = MonthlyPartitions.Plan(
            "audit_event",
            new DateTimeOffset(2026, 8, 22, 10, 30, 0, TimeSpan.Zero),
            monthsAhead: 2);

        windows.Count.ShouldBe(3);

        windows[0].PartitionName.ShouldBe("audit_event_2026_08");
        windows[0].FromInclusive.ShouldBe(new DateOnly(2026, 8, 1));
        windows[0].ToExclusive.ShouldBe(new DateOnly(2026, 9, 1));

        windows[1].PartitionName.ShouldBe("audit_event_2026_09");
        windows[1].FromInclusive.ShouldBe(new DateOnly(2026, 9, 1));
        windows[1].ToExclusive.ShouldBe(new DateOnly(2026, 10, 1));

        windows[2].PartitionName.ShouldBe("audit_event_2026_10");
        windows[2].FromInclusive.ShouldBe(new DateOnly(2026, 10, 1));
        windows[2].ToExclusive.ShouldBe(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void Rolls_the_year_over_when_the_window_crosses_december()
    {
        IReadOnlyList<MonthlyPartitionWindow> windows = MonthlyPartitions.Plan(
            "audit_event",
            new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero),
            monthsAhead: 2);

        windows.Select(window => window.PartitionName).ShouldBe(
            ["audit_event_2026_11", "audit_event_2026_12", "audit_event_2027_01"]);

        windows[2].FromInclusive.ShouldBe(new DateOnly(2027, 1, 1));
        windows[2].ToExclusive.ShouldBe(new DateOnly(2027, 2, 1));
    }

    [Fact]
    public void Uses_the_utc_month_of_the_reference_instant_not_the_local_one()
    {
        // 2026-09-01T01:30+03:00 is 2026-08-31T22:30 in UTC, so the plan
        // starts in August even though the local wall clock reads September.
        IReadOnlyList<MonthlyPartitionWindow> windows = MonthlyPartitions.Plan(
            "audit_event",
            new DateTimeOffset(2026, 9, 1, 1, 30, 0, TimeSpan.FromHours(3)),
            monthsAhead: 1);

        windows[0].PartitionName.ShouldBe("audit_event_2026_08");
        windows[1].PartitionName.ShouldBe("audit_event_2026_09");
    }

    [Fact]
    public void Names_carry_the_table_prefix_so_every_configured_table_gets_its_own_partitions()
    {
        IReadOnlyList<MonthlyPartitionWindow> windows = MonthlyPartitions.Plan(
            "delivery_event",
            new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero),
            monthsAhead: 0);

        windows.ShouldHaveSingleItem().PartitionName.ShouldBe("delivery_event_2026_02");
    }
}
