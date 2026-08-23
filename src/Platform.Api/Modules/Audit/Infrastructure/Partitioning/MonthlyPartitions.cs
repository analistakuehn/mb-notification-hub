namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// One month-long partition window. Boundaries are calendar days at midnight
/// UTC: the lower bound is inclusive, the upper bound exclusive, matching
/// PostgreSQL RANGE partition semantics.
/// </summary>
internal sealed record MonthlyPartitionWindow(
    string PartitionName,
    DateOnly FromInclusive,
    DateOnly ToExclusive);

/// <summary>
/// Pure calculation of the monthly partition windows a table must have. Months
/// are taken from the UTC instant of the reference clock, so every host plans
/// the same windows regardless of its local time zone.
/// </summary>
internal static class MonthlyPartitions
{
    /// <summary>
    /// Plans the windows from the reference month through <paramref name="monthsAhead"/>
    /// months after it, inclusive. Partition names follow
    /// <c>{table}_{year}_{month:00}</c>, the same shape the conversion
    /// migration uses, so both sides always agree on the object names.
    /// </summary>
    internal static IReadOnlyList<MonthlyPartitionWindow> Plan(
        string table,
        DateTimeOffset reference,
        int monthsAhead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfNegative(monthsAhead);

        DateTime utc = reference.UtcDateTime;
        var referenceMonth = new DateOnly(utc.Year, utc.Month, 1);

        var windows = new List<MonthlyPartitionWindow>(monthsAhead + 1);
        for (var offset = 0; offset <= monthsAhead; offset++)
        {
            DateOnly from = referenceMonth.AddMonths(offset);
            windows.Add(new MonthlyPartitionWindow(
                $"{table}_{from.Year:D4}_{from.Month:D2}",
                from,
                from.AddMonths(1)));
        }

        return windows;
    }
}
