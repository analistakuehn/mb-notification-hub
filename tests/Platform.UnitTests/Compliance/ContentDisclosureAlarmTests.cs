using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NSubstitute;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The other half of the containment on the content route. The rate limit caps
/// how fast one principal may open stored content; the alarm notices the
/// patient sweep that never trips a per-minute ceiling.
/// </summary>
public sealed class ContentDisclosureAlarmTests
{
    private const string Principal = "auditor-1";

    [Fact]
    public void The_alarm_stays_quiet_below_the_threshold()
    {
        (ContentDisclosureAlarm alarm, List<string> warnings, _) = Build(threshold: 3);

        alarm.RecordDisclosure(Principal);
        alarm.RecordDisclosure(Principal);

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void The_alarm_fires_once_on_the_crossing_and_not_on_every_read_after_it()
    {
        (ContentDisclosureAlarm alarm, List<string> warnings, _) = Build(threshold: 3);

        for (var opening = 0; opening < 6; opening++)
        {
            alarm.RecordDisclosure(Principal);
        }

        // A principal parked above the threshold would otherwise log once per
        // read and drown the signal it exists to raise.
        warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void A_new_window_starts_the_count_over()
    {
        (ContentDisclosureAlarm alarm, List<string> warnings, FakeClock clock) = Build(threshold: 2);

        alarm.RecordDisclosure(Principal);
        clock.Advance(TimeSpan.FromMinutes(11));
        alarm.RecordDisclosure(Principal);

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Each_principal_carries_its_own_count()
    {
        (ContentDisclosureAlarm alarm, List<string> warnings, _) = Build(threshold: 2);

        alarm.RecordDisclosure(Principal);
        alarm.RecordDisclosure("auditor-2");

        warnings.ShouldBeEmpty();

        alarm.RecordDisclosure(Principal);
        warnings.Count.ShouldBe(1);
    }

    private static (ContentDisclosureAlarm Alarm, List<string> Warnings, FakeClock Clock) Build(int threshold)
    {
        var warnings = new List<string>();
        ILogger<ContentDisclosureAlarm> logger = Substitute.For<ILogger<ContentDisclosureAlarm>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        logger
            .When(instance => instance.Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(),
                Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(call => warnings.Add(call[1]!.ToString()!));

        var clock = new FakeClock(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        IOptions<ContentDisclosureAlarmOptions> options = Options.Create(new ContentDisclosureAlarmOptions
        {
            Threshold = threshold,
            WindowMinutes = 10,
        });
        return (new ContentDisclosureAlarm(options, clock, logger), warnings, clock);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
